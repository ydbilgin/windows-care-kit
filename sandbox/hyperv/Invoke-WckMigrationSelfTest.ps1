#requires -Version 7.0
<#
.SYNOPSIS
    Run the HOST-SAFE MigrationE2E --selftest INSIDE the disposable 'WCK-E2E' VM, to prove the
    M4 restore + journal/undo machinery works on a SECOND, real Windows 11 (real OS / NTFS /
    elevated PSDirect context). Then reset the VM.

.DESCRIPTION
    This is the SAFE VM proof (council 2026-06-25: the destructive real-%USERPROFILE% mode was
    REJECTED — net safety downgrade for marginal evidence). The selftest is fully host-safe: it
    self-seeds under a guest TEMP root and never touches the guest's real profile, so there is no
    real-folder-overwrite risk anywhere. The VM only gives "a real, separate Windows machine".

      1. (optional -Publish / auto if missing) publish MigrationE2E self-contained win-x64.
      2. Guarded preflight: Assert-WckCampaignReady (marker in .Notes + checkpoint present).
      3. Restore 'baseline-campaign' (NOT baseline-clean = broken vTPM); boot; wait for PSDirect.
      4. Push the self-contained exe into the guest.
      5. Run  MigrationE2E.exe --selftest --root C:\WCK-SelfTest\run  (host-safe; guest temp only).
      6. Pull the evidence dir to <OutputBase>.
      7. finally: GUARDED reset to baseline-campaign (Assert-WckDisposableVM before any force-op).

    HOST SAFETY: touches only the VM named -VMName and writes evidence under -OutputBase. The guest
    payload writes ONLY under C:\WCK-SelfTest (guest temp). No host program/registry/profile change,
    no reboot. Disposable VM is reverted on exit.
#>
[CmdletBinding()]
param(
    [string] $VMName        = 'WCK-E2E',
    [string] $Checkpoint    = 'baseline-campaign',           # vTPM-safe (baseline-clean is broken post-format)
    [string] $HarnessDir    = 'F:\WCK-VM\harness\mig-selftest',
    [string] $OutputBase    = 'F:\WCK-VM\selftest-output',
    [int]    $ReadyTimeoutMin = 10,
    [switch] $Publish,
    [switch] $KeepDirtyState # debug ONLY: skip the final checkpoint restore
)

$ErrorActionPreference = 'Stop'
function Step([string]$m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info([string]$m){ Write-Host "    $m" -ForegroundColor DarkGray }

# Fail-closed disposable guard (marker in .Notes + path-under-root + checkpoint present).
. (Join-Path $PSScriptRoot 'campaign\Guard-WckDisposable.ps1')

# --- preflight (guarded) ----------------------------------------------------
if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { throw "Hyper-V module unavailable." }
$ready = Assert-WckCampaignReady -VMName $VMName -Checkpoint $Checkpoint   # THROWS unless marker+checkpoint present
Info "campaign guid: $($ready.CampaignGuid); checkpoint: $($ready.Checkpoint)"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnet   = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
$exeName  = 'MigrationE2E.exe'

# --- 1. publish self-contained (guest has no .NET 10 SDK) -------------------
if ($Publish -or -not (Test-Path (Join-Path $HarnessDir $exeName))) {
    Step "Publishing $exeName (self-contained win-x64) -> $HarnessDir ..."
    if (-not (Test-Path $dotnet)) { throw "Per-user .NET 10 SDK not found at $dotnet." }
    $csproj = Join-Path $repoRoot 'tools\MigrationE2E\MigrationE2E.csproj'
    & $dotnet publish $csproj -c Release --output $HarnessDir --runtime win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "$exeName publish failed ($LASTEXITCODE)." }
}
if (-not (Test-Path (Join-Path $HarnessDir $exeName))) { throw "Harness exe missing after publish: $HarnessDir\$exeName" }

# --- evidence dir (default base only, or empty) -----------------------------
$out = Join-Path $OutputBase 'selftest'
if (Test-Path $out) {
    $isDefault = ($OutputBase -eq 'F:\WCK-VM\selftest-output')
    $isEmpty   = -not (Get-ChildItem -LiteralPath $out -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not ($isDefault -or $isEmpty)) { throw "Output dir '$out' is non-empty and not the default; refuse to clear." }
    Remove-Item $out -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

# Throwaway autologon credential for the disposable, network-isolated WCK-E2E guest (reverted on every
# checkpoint, no value outside the VM). NOT a real secret.
$sec  = ConvertTo-SecureString 'WckE2E!2026' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('wck', $sec)

function Restore-BaselineGuarded {
    # Guarded: only a VM carrying the campaign marker may be force-stopped / checkpoint-restored.
    Assert-WckDisposableVM -VMName $VMName | Out-Null
    if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
    Restore-VMCheckpoint -VMName $VMName -Name $Checkpoint -Confirm:$false
}

$selftestExit = $null
try {
    # --- 2. pristine guest ---
    Step "Restoring '$Checkpoint' checkpoint..."
    Restore-BaselineGuarded

    # --- 3. boot + wait for PowerShell Direct ---
    Step "Starting VM; waiting for PowerShell Direct..."
    Start-VM -Name $VMName
    $deadline = (Get-Date).AddMinutes($ReadyTimeoutMin); $reachable = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        try { if (Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { Test-Path 'C:\wck-ready.txt' } -ErrorAction Stop) { $reachable = $true; break } }
        catch { Info "guest not reachable yet..." }
    }
    if (-not $reachable) { throw "Guest not ready within $ReadyTimeoutMin min." }
    Step "Guest READY."

    $session = New-PSSession -VMName $VMName -Credential $cred
    try {
        # --- 4. push the self-contained harness into the guest ---
        Step "Pushing MigrationE2E publish into the guest..."
        Invoke-Command -Session $session -ScriptBlock {
            Remove-Item 'C:\WCK-SelfTest' -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path 'C:\WCK-SelfTest\bin' | Out-Null
        }
        Copy-Item -Path (Join-Path $HarnessDir '*') -Destination 'C:\WCK-SelfTest\bin' -ToSession $session -Recurse -Force

        # --- 5. run the HOST-SAFE selftest in the guest. The selftest's own structural floor
        #        (PrepareCleanDirectory) REQUIRES the --root be under a temp dir, so use the guest
        #        LocalAppData\Temp; the guest returns the resolved evidence dir for the pull. ---
        Step "Running MigrationE2E --selftest inside the guest..."
        $res = Invoke-Command -Session $session -ScriptBlock {
            $env:TEMP = "$env:LOCALAPPDATA\Temp"; $env:TMP = "$env:LOCALAPPDATA\Temp"
            $root = Join-Path $env:LOCALAPPDATA 'Temp\wck-selftest-run'
            $stdout = & 'C:\WCK-SelfTest\bin\MigrationE2E.exe' --selftest --root $root 2>&1 | Out-String
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; StdOut = $stdout; EvidenceDir = (Join-Path $root 'migration-e2e-selftest\output') }
        }
        $selftestExit = $res.ExitCode
        Info "selftest exit code: $selftestExit"
        # persist the in-guest console output alongside the evidence for provenance
        Set-Content -Path (Join-Path $out 'selftest-console.txt') -Value $res.StdOut -Encoding utf8

        # --- 6. pull evidence (from the guest-resolved temp evidence dir) ---
        Step "Pulling evidence to $out ..."
        Copy-Item -Path (Join-Path $res.EvidenceDir '*') -Destination $out -FromSession $session -Recurse -Force -ErrorAction SilentlyContinue
        # Make a missed pull LOUD (auditor MINOR): a PASS exit with no evidence on disk must not read as green.
        if ($selftestExit -eq 0 -and -not (Test-Path (Join-Path $out 'migration-e2e-evidence.json'))) {
            throw "selftest reported exit 0 but no evidence JSON was pulled from '$($res.EvidenceDir)' — refusing to claim a green run."
        }
    } finally {
        Remove-PSSession $session -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Write-Host '================================================================' -ForegroundColor Green
    Write-Host " WCK MigrationE2E --selftest in disposable VM '$VMName'" -ForegroundColor Green
    Write-Host ("   selftest : exit {0}  -> {1}" -f $selftestExit, ($selftestExit -eq 0 ? 'PASS' : 'FAIL/NON-ZERO')) -ForegroundColor Green
    Write-Host "   evidence : $out" -ForegroundColor Green
    Write-Host '================================================================' -ForegroundColor Green
}
finally {
    if (-not $KeepDirtyState) {
        Step "Resetting VM to clean baseline (discard run)..."
        try { Restore-BaselineGuarded }
        catch { Write-Host "    cleanup warning: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}

if ($selftestExit -ne 0) { exit 1 }
exit 0
