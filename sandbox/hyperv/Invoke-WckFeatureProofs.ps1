#requires -Version 7.0
<#
.SYNOPSIS
    WCK feature-proofs - per-run orchestration over PowerShell Direct (autonomous).

.DESCRIPTION
    Drives BOTH live feature proofs against the disposable 'WCK-E2E' VM in a SINGLE
    checkpoint cycle, then resets it. Needs Hyper-V Administrators rights (the elevated
    session already has this):

      1. (optional -Publish) publish MigrationE2E + CleanE2E (self-contained win-x64).
      2. Restore the 'baseline-clean' checkpoint (pristine guest).
      3. Start the VM; wait for PowerShell Direct + C:\wck-ready.txt.
      4. Push each harness publish + its guest-run script over the VMBus (guest offline):
           C:\WCK-MigE2E\   <- MigrationE2E publish + migration-guest-run.ps1
           C:\WCK-CleanE2E\ <- CleanE2E publish    + clean-guest-run.ps1
      5. Run migration-guest-run.ps1  (backup -> package/zip -> restore round-trip;
         non-vacuous secret/cache exclusion; projects/skills land in the package).
      6. Run clean-guest-run.ps1      (--execute: P1 junk deleted, P2 System32 untouched,
         P3 HKCU Run value deleted + key-delete refused).
      7. Pull evidence: C:\WCK-MigOutput -> <OutputBase>\mig ; C:\WCK-CleanOutput -> <OutputBase>\clean.
      8. Restore the baseline checkpoint (discard the dirty run) and stop the VM.

    HOST SAFETY: touches only the VM named -VMName and writes evidence under -OutputBase.
    No host program/registry/profile change, no reboot. Every destructive step happens
    inside the disposable guest, gated on its own WCK_DISPOSABLE_MACHINE signal.
#>
[CmdletBinding()]
param(
    [string] $VMName          = 'WCK-E2E',
    [string] $Checkpoint      = 'baseline-clean',
    [string] $MigHarnessDir   = 'F:\WCK-VM\harness\mig',
    [string] $CleanHarnessDir = 'F:\WCK-VM\harness\clean',
    [string] $OutputBase      = 'F:\WCK-VM\proof-output',
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
catch { throw "VM '$VMName' not found / no Hyper-V access. Build it with Build-WckVM.ps1." }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnet   = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"

function Publish-Harness([string]$csprojRel, [string]$outDir, [string]$exeName) {
    if ($Publish -or -not (Test-Path (Join-Path $outDir $exeName))) {
        Step "Publishing $exeName (self-contained win-x64) -> $outDir ..."
        if (-not (Test-Path $dotnet)) { throw "Per-user .NET 10 SDK not found at $dotnet." }
        $csproj = Join-Path $repoRoot $csprojRel
        & $dotnet publish $csproj -c Release --output $outDir --runtime win-x64 --self-contained true
        if ($LASTEXITCODE -ne 0) { throw "$exeName publish failed ($LASTEXITCODE)." }
    }
}

Publish-Harness 'tools\MigrationE2E\MigrationE2E.csproj' $MigHarnessDir  'MigrationE2E.exe'
Publish-Harness 'tools\CleanE2E\CleanE2E.csproj'         $CleanHarnessDir 'CleanE2E.exe'

# clean host output dirs (default base only, or empty)
$migOut   = Join-Path $OutputBase 'mig'
$cleanOut = Join-Path $OutputBase 'clean'
foreach ($d in @($migOut, $cleanOut)) {
    if (Test-Path $d) {
        $isDefault = ($OutputBase -eq 'F:\WCK-VM\proof-output')
        $isEmpty   = -not (Get-ChildItem -LiteralPath $d -ErrorAction SilentlyContinue | Select-Object -First 1)
        if (-not ($isDefault -or $isEmpty)) { throw "Output dir '$d' is non-empty and not the default; refuse to clear." }
        Remove-Item $d -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

# Throwaway autologon credential for the disposable, network-isolated WCK-E2E guest —
# reverted on every checkpoint, no network-exposed service, no value outside the VM. NOT a real secret.
$sec  = ConvertTo-SecureString 'WckE2E!2026' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('wck', $sec)

function Restore-Baseline {
    $cp = Get-VMCheckpoint -VMName $VMName -Name $Checkpoint -ErrorAction SilentlyContinue
    if (-not $cp) { throw "Checkpoint '$Checkpoint' not found on '$VMName'." }
    if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
    Restore-VMCheckpoint -VMName $VMName -Name $Checkpoint -Confirm:$false
}

$migExit = $null; $cleanExit = $null
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

    $session = New-PSSession -VMName $VMName -Credential $cred
    try {
        # --- 3. push payloads over the VMBus ---
        Step "Pushing harnesses + guest-run scripts into the guest..."
        Invoke-Command -Session $session -ScriptBlock {
            Remove-Item 'C:\WCK-MigE2E','C:\WCK-CleanE2E','C:\WCK-MigOutput','C:\WCK-CleanOutput' -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path 'C:\WCK-MigE2E','C:\WCK-CleanE2E','C:\WCK-MigOutput','C:\WCK-CleanOutput' | Out-Null
        }
        Copy-Item -Path (Join-Path $MigHarnessDir '*')   -Destination 'C:\WCK-MigE2E'   -ToSession $session -Recurse -Force
        Copy-Item -Path (Join-Path $CleanHarnessDir '*') -Destination 'C:\WCK-CleanE2E' -ToSession $session -Recurse -Force
        Copy-Item -Path (Join-Path $PSScriptRoot 'migration-guest-run.ps1') -Destination 'C:\WCK-MigE2E\migration-guest-run.ps1' -ToSession $session -Force
        Copy-Item -Path (Join-Path $PSScriptRoot 'clean-guest-run.ps1')     -Destination 'C:\WCK-CleanE2E\clean-guest-run.ps1'   -ToSession $session -Force

        # --- 4. MIGRATION proof ---
        Step "Running migration-guest-run.ps1 inside the guest..."
        $migResult = Invoke-Command -Session $session -ScriptBlock {
            Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction SilentlyContinue
            & 'C:\WCK-MigE2E\migration-guest-run.ps1' -Harness 'C:\WCK-MigE2E\MigrationE2E.exe' -Base 'C:\MigE2E' -Output 'C:\WCK-MigOutput'
        }
        $migExit = $migResult.ExitCode
        Info "migration exit code: $migExit"

        # --- 5. CLEAN proof ---
        Step "Running clean-guest-run.ps1 inside the guest (--execute)..."
        $cleanResult = Invoke-Command -Session $session -ScriptBlock {
            Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction SilentlyContinue
            & 'C:\WCK-CleanE2E\clean-guest-run.ps1' -Harness 'C:\WCK-CleanE2E\CleanE2E.exe' -Output 'C:\WCK-CleanOutput'
        }
        $cleanExit = $cleanResult.ExitCode
        Info "clean exit code: $cleanExit"

        # --- 6. pull evidence (+ the migration package zip, so package contents can be audited:
        #        benign skills/projects files must be PRESENT, the 8 secret/cache items ABSENT) ---
        Step "Pulling evidence to $OutputBase ..."
        Copy-Item -Path 'C:\WCK-MigOutput\*'   -Destination $migOut   -FromSession $session -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item -Path 'C:\WCK-CleanOutput\*' -Destination $cleanOut -FromSession $session -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item -Path 'C:\MigE2E\Pkg\migration-export.zip' -Destination $migOut -FromSession $session -Force -ErrorAction SilentlyContinue
    } finally {
        Remove-PSSession $session -ErrorAction SilentlyContinue
    }

    # --- report (exit codes are ground truth; 0 == PASS for both harnesses) ---
    Write-Host ''
    Write-Host '================================================================' -ForegroundColor Green
    Write-Host " WCK feature proofs" -ForegroundColor Green
    Write-Host ("   migration : exit {0}  -> {1}" -f $migExit,   ($migExit   -eq 0 ? 'PASS' : 'FAIL/NON-ZERO')) -ForegroundColor Green
    Write-Host ("   clean     : exit {0}  -> {1}" -f $cleanExit, ($cleanExit -eq 0 ? 'PASS' : 'FAIL/NON-ZERO')) -ForegroundColor Green
    Write-Host "   evidence  : $OutputBase (\mig, \clean)" -ForegroundColor Green
    Write-Host '================================================================' -ForegroundColor Green
}
finally {
    if (-not $KeepDirtyState) {
        Step "Resetting VM to clean baseline (discard run)..."
        try { Restore-Baseline; if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force } }
        catch { Write-Host "    cleanup warning: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}

if ($migExit -ne 0 -or $cleanExit -ne 0) { exit 1 }
exit 0
