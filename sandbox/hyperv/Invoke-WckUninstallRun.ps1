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
    [switch] $KeepDirtyState,    # debug: skip the final checkpoint restore
    [securestring] $GuestPassword,
    # FIX-D: the campaign cell-runner owns the SINGLE checkpoint-epoch — it does the guarded
    # restore ITSELF before snapshotting BEFORE, then calls this runner with -SkipInitialRestore
    # so the inner flow does NOT restore on top of the cell-runner's pristine epoch.
    [switch] $SkipInitialRestore,
    # FIX-D: when set, every delegated Stop-VM -TurnOff / Restore here passes the C-16 marker
    # gate first (the cell-runner dot-sources Guard-WckDisposable.ps1 into this process).
    [switch] $Campaign
)

$ErrorActionPreference = 'Stop'
function Step([string]$m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info([string]$m){ Write-Host "    $m" -ForegroundColor DarkGray }

# FIX-D: in campaign mode the marker guard MUST already be in scope (dot-sourced by the
# cell-runner). Assert it before any delegated force-op; load it directly as a fallback.
# SCOPE FIX: the cell-runner's dot-source does NOT cross the `& script.ps1` invocation
# boundary for $script:-scoped vars (the guard FUNCTIONS are inherited, but $script:WckCampaignRoot
# is not). Dot-source the guard at THIS script's scope in campaign mode so the pinned root is set
# here before any Assert-WckPathUnderRoot call.
if ($Campaign) { . (Join-Path $PSScriptRoot 'campaign\Guard-WckDisposable.ps1') }

function Assert-CampaignDisposable([string]$name) {
    if (-not $Campaign) { return }
    if (-not (Get-Command Assert-WckDisposableVM -ErrorAction SilentlyContinue)) {
        . (Join-Path $PSScriptRoot 'campaign\Guard-WckDisposable.ps1')
    }
    $null = Assert-WckDisposableVM -VMName $name
}
function Assert-CampaignPath([string]$path) {
    if (-not $Campaign) { return }
    if (-not (Get-Command Assert-WckPathUnderRoot -ErrorAction SilentlyContinue)) {
        . (Join-Path $PSScriptRoot 'campaign\Guard-WckDisposable.ps1')
    }
    $null = Assert-WckPathUnderRoot -Path $path
}

function Get-GuestCredential {
    if ($GuestPassword) {
        Info "guest credential source: -GuestPassword (disposable VM only)"
        return [System.Management.Automation.PSCredential]::new('wck', $GuestPassword)
    }
    $pw = $env:WCK_GUEST_CRED
    if ([string]::IsNullOrEmpty($pw)) {
        throw "Guest credential required: provide -GuestPassword (SecureString) or env:WCK_GUEST_CRED; refusing to use a script-literal default."
    }
    Info "guest credential source: env:WCK_GUEST_CRED (disposable VM only)"
    $sec = ConvertTo-SecureString $pw -AsPlainText -Force
    [System.Management.Automation.PSCredential]::new('wck', $sec)
}

$cred = Get-GuestCredential

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
    Assert-CampaignPath $OutputDir
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function Restore-Baseline {
    Assert-CampaignDisposable $VMName   # FIX-D: marker gate before any TurnOff/restore in campaign mode
    $cp = Get-VMCheckpoint -VMName $VMName -Name $Checkpoint -ErrorAction SilentlyContinue
    if (-not $cp) { throw "Checkpoint '$Checkpoint' not found on '$VMName'." }
    if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
    Restore-VMCheckpoint -VMName $VMName -Name $Checkpoint -Confirm:$false
}

try {
    # --- 1. pristine guest ---
    # FIX-D: in campaign mode the cell-runner already restored to the pristine epoch and took
    # its BEFORE snapshot; restoring AGAIN here would start a SECOND epoch (BEFORE/mutate/AFTER
    # would no longer share one checkpoint-epoch). Skip the initial restore in that case.
    if ($SkipInitialRestore) {
        Step "Skipping initial restore (campaign cell-runner owns the checkpoint-epoch)."
    } else {
        Step "Restoring '$Checkpoint' checkpoint..."
        Restore-Baseline
    }

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
        try { Restore-Baseline; Assert-CampaignDisposable $VMName; if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force } }
        catch { Write-Host "    cleanup warning: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}
