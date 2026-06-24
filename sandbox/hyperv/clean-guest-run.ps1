<#
  clean-guest-run.ps1 — runs INSIDE the WCK-E2E guest (over PowerShell Direct).
  Seeds the disposable-machine signal, then runs the CleanE2E harness to prove the
  Clean module's production safety pipeline (junk delete + startup-entry disable)
  end-to-end on a real Windows machine.

  Proves: P1 real junk dir deleted via RecycleBinFileDeleteAdapter,
          P2 System32 path refused by gate and still present after the run,
          P3 HKCU Run value deleted via RegistryDeleteAdapter + key-delete refused.

  Offline / self-contained: no installs needed.
#>
[CmdletBinding()]
param(
    [string] $Harness = 'C:\WCK-CleanE2E\CleanE2E.exe',
    [string] $Output  = 'C:\WCK-CleanOutput',
    [string] $JunkDir,
    [string] $RunValueName
)
$ErrorActionPreference = 'Stop'

# --- Disposable-machine opt-in: both the env var and the marker file are required. ---
$env:WCK_DISPOSABLE_MACHINE = '1'
$marker = Join-Path $env:TEMP 'wck-disposable.marker'
Set-Content $marker 'disposable' -Encoding ascii

Write-Host "[clean-guest] WCK_DISPOSABLE_MACHINE=1, marker=$marker"
if (-not (Test-Path $Harness)) { throw "harness not found: $Harness" }

$argsList = @('--execute', '--output', $Output)
if (-not [string]::IsNullOrWhiteSpace($JunkDir)) { $argsList += @('--junkDir', $JunkDir) }
if (-not [string]::IsNullOrWhiteSpace($RunValueName)) { $argsList += @('--runValueName', $RunValueName) }
& $Harness @argsList
$rc = $LASTEXITCODE
Write-Host "[clean-guest] CleanE2E.exe exit code: $rc"
[pscustomobject]@{ ExitCode = $rc }
