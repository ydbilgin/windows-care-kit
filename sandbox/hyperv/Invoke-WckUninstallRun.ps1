#requires -Version 7.0
<#
.SYNOPSIS
    WCK Uninstall E2E - per-run orchestration over PowerShell Direct (autonomous).

.DESCRIPTION
    Drives one full uninstall-E2E run against the disposable 'WCK-E2E' VM and resets
    it afterward. Needs only Hyper-V Administrators rights (NOT full elevation), so it
    runs from a normal session once the user is in that group:

      1. Restore the 'baseline-clean' checkpoint (pristine guest).
      2. Start the VM; wait for PowerShell Direct + C:\wck-ready.txt.
      3. Push harness + pre-staged installers + guest-run.ps1 into C:\WCK-Input
         (over the VMBus; the guest stays offline).
      4. Invoke guest-run.ps1 inside the guest (installs 4 KINDs, runs UninstallE2E,
         actually uninstalls git+vscode, re-reads the registry to prove gone).
      5. Pull evidence from C:\WCK-Output back to -OutputDir on the host.
      6. Restore the baseline checkpoint again (discard the dirty run) and stop the VM.

    HOST SAFETY: touches only the VM named -VMName and writes evidence under -OutputDir.
    No host program/registry/profile change, no reboot.

.PARAMETER Publish
    Re-publish the UninstallE2E harness (self-contained win-x64) before the run.
    Off by default; required at least once (or point -HarnessDir at an existing publish).
#>
[CmdletBinding()]
param(
    [string] $VMName       = 'WCK-E2E',
    [string] $Checkpoint   = 'baseline-clean',
    [string] $InstallersDir = 'F:\WCK-VM\installers',
    [string] $HarnessDir   = 'C:\WCK-UninstallStaging\harness',
    [string] $OutputDir    = 'C:\WCK-UninstallOutput',
    [int]    $ReadyTimeoutMin = 10,
    [switch] $Publish,
    [switch] $KeepDirtyState   # debug: skip the final checkpoint restore
)

$ErrorActionPreference = 'Stop'
function Step([string]$m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info([string]$m){ Write-Host "    $m" -ForegroundColor DarkGray }

# --- preflight --------------------------------------------------------------
if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { throw "Hyper-V module unavailable." }
try { Get-VM -Name $VMName -ErrorAction Stop | Out-Null }
catch { throw "VM '$VMName' not found / no Hyper-V access. Build it with Build-WckVM.ps1 and ensure you are in 'Hyper-V Administrators' (new logon session)." }

if ($Publish -or -not (Test-Path (Join-Path $HarnessDir 'UninstallE2E.exe'))) {
    Step "Publishing UninstallE2E harness (self-contained win-x64)..."
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { throw "Per-user .NET 10 SDK not found at $dotnet." }
    $csproj = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'tools\UninstallE2E\UninstallE2E.csproj'
    & $dotnet publish $csproj -c Debug --output $HarnessDir --runtime win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "Harness publish failed ($LASTEXITCODE)." }
}
if (-not (Test-Path (Join-Path $InstallersDir '7z.msi'))) { throw "Installers not found in $InstallersDir (run the host pre-download)." }

# clean host output dir (default path only, or empty)
if (Test-Path $OutputDir) {
    $isDefault = ($OutputDir -eq 'C:\WCK-UninstallOutput')
    $isEmpty   = -not (Get-ChildItem -LiteralPath $OutputDir -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not ($isDefault -or $isEmpty)) { throw "OutputDir '$OutputDir' is non-empty and not the default; refuse to clear." }
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$sec  = ConvertTo-SecureString 'WckE2E!2026' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('wck', $sec)

function Restore-Baseline {
    $cp = Get-VMCheckpoint -VMName $VMName -Name $Checkpoint -ErrorAction SilentlyContinue
    if (-not $cp) { throw "Checkpoint '$Checkpoint' not found on '$VMName'." }
    if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
    Restore-VMCheckpoint -VMName $VMName -Name $Checkpoint -Confirm:$false
}

try {
    # --- 1. pristine guest ---
    Step "Restoring '$Checkpoint' checkpoint..."
    Restore-Baseline

    # --- 2. boot + wait for PowerShell Direct ---
    Step "Starting VM; waiting for PowerShell Direct..."
    Start-VM -Name $VMName
    $deadline = (Get-Date).AddMinutes($ReadyTimeoutMin); $ready = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        try { if (Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { Test-Path 'C:\wck-ready.txt' } -ErrorAction Stop) { $ready = $true; break } }
        catch { Info "guest not reachable yet..." }
    }
    if (-not $ready) { throw "Guest not ready within $ReadyTimeoutMin min." }
    Step "Guest READY."

    # --- 3. push payload over the VMBus ---
    Step "Pushing harness + installers + runner into the guest (PowerShell Direct)..."
    $session = New-PSSession -VMName $VMName -Credential $cred
    try {
        Invoke-Command -Session $session -ScriptBlock {
            Remove-Item 'C:\WCK-Input','C:\WCK-Output' -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path 'C:\WCK-Input\installers','C:\WCK-Input\harness','C:\WCK-Output' | Out-Null
        }
        Copy-Item -Path (Join-Path $HarnessDir '*')   -Destination 'C:\WCK-Input\harness'    -ToSession $session -Recurse -Force
        Copy-Item -Path (Join-Path $InstallersDir '*') -Destination 'C:\WCK-Input\installers' -ToSession $session -Recurse -Force
        Copy-Item -Path (Join-Path $PSScriptRoot 'guest-run.ps1') -Destination 'C:\WCK-Input\guest-run.ps1' -ToSession $session -Force

        # --- 4. run inside the guest ---
        Step "Running guest-run.ps1 inside the guest (install 4 KINDs + uninstall git,vscode)..."
        $runResult = Invoke-Command -Session $session -ScriptBlock {
            # Fresh Win11 blocks unsigned .ps1 by default; allow for THIS process only.
            Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction SilentlyContinue
            & 'C:\WCK-Input\guest-run.ps1' -InputDir 'C:\WCK-Input' -Output 'C:\WCK-Output'
        }
        Info "guest result: $($runResult.Result) (exit $($runResult.ExitCode))"

        # --- 5. pull evidence ---
        Step "Pulling evidence to $OutputDir ..."
        Copy-Item -Path 'C:\WCK-Output\*' -Destination $OutputDir -FromSession $session -Recurse -Force
    } finally {
        Remove-PSSession $session -ErrorAction SilentlyContinue
    }

    # --- report ---
    $resultFile = Join-Path $OutputDir 'uninstall-e2e-result.txt'
    $result = (Test-Path $resultFile) ? (Get-Content $resultFile -Raw).Trim() : '(no result file)'
    Write-Host ''
    Write-Host '================================================================' -ForegroundColor Green
    Write-Host " Uninstall E2E run: $result" -ForegroundColor Green
    Write-Host "   evidence : $OutputDir" -ForegroundColor Green
    Write-Host '================================================================' -ForegroundColor Green
}
finally {
    if (-not $KeepDirtyState) {
        Step "Resetting VM to clean baseline (discard run)..."
        try { Restore-Baseline; if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force } }
        catch { Write-Host "    cleanup warning: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}
