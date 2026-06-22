<#
.SYNOPSIS
    Runs INSIDE the WCK-E2E guest (invoked over PowerShell Direct). Installs the
    four real programs from PRE-STAGED local files (no guest internet), then runs
    the UninstallE2E harness and returns a structured result.

.DESCRIPTION
    PowerShell Direct authenticates as the guest's local admin 'wck' with a full
    (elevated) token, so machine-wide MSI/Inno/NSIS installs run with no UAC prompt
    and no self-elevation dance (unlike the Windows Sandbox .cmd runner).

    Expects, pushed in by the host beforehand:
      C:\WCK-Input\harness\UninstallE2E.exe   (self-contained)
      C:\WCK-Input\installers\{7z.msi,git.exe,npp.exe,vscode.exe}
    Writes evidence to C:\WCK-Output (pulled back to the host afterward).

    Returns a PSCustomObject { ExitCode; Result; Installs[] } so the host harness
    decides PASS/FAIL from ground truth, not from a banner string.
#>
[CmdletBinding()]
param(
    [string] $InputDir = 'C:\WCK-Input',
    [string] $Output   = 'C:\WCK-Output'
)

$ErrorActionPreference = 'Continue'
$installers = Join-Path $InputDir 'installers'
$harness    = Join-Path $InputDir 'harness\UninstallE2E.exe'
New-Item -ItemType Directory -Force -Path $Output | Out-Null

function Log([string]$m){
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $Output 'guest-progress.log') -Value $line
}

# --- disposable-machine opt-in (REQUIRED for the harness --execute guard) ----
# Same signal step4/UninstallE2E look for; exists only inside this disposable VM.
$env:WCK_DISPOSABLE_MACHINE = '1'
'disposable' | Set-Content -LiteralPath (Join-Path $env:TEMP 'wck-disposable.marker')
Log "disposable-machine opt-in set (env + marker)."

# --- install four real program KINDS from local files ------------------------
$results = @()
function Install-One([string]$name, [string]$file, [scriptblock]$run){
    $path = Join-Path $installers $file
    if (-not (Test-Path $path)) { Log "MISSING installer: $file (skipping $name)"; $script:results += [pscustomobject]@{Name=$name;Exit='missing'}; return }
    Log "installing $name from $file ..."
    $code = & $run $path
    Log "  $name installer exit: $code"
    $script:results += [pscustomobject]@{ Name=$name; Exit=$code }
}

Install-One '7-Zip (MSI machine-wide)'  '7z.msi'   { param($p) (Start-Process msiexec.exe -ArgumentList "/i `"$p`" /qn /norestart" -Wait -PassThru).ExitCode }
Install-One 'Git (Inno machine-wide)'    'git.exe'  { param($p) (Start-Process $p -ArgumentList '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /NOCANCEL /SP-' -Wait -PassThru).ExitCode }
Install-One 'Notepad++ (NSIS machine-wide)' 'npp.exe' { param($p) (Start-Process $p -ArgumentList '/S' -Wait -PassThru).ExitCode }
Install-One 'VS Code (Inno per-user)'    'vscode.exe' { param($p) (Start-Process $p -ArgumentList '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /MERGETASKS=!runcode' -Wait -PassThru).ExitCode }

# VS Code user-setup auto-launches; a running Code.exe makes its uninstaller pop a
# "close all copies" modal that hangs an unattended run. Kill it before the harness.
Log "closing any auto-launched VS Code so its uninstaller does not block..."
Get-Process Code,'Code - Insiders' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Log "installs done; settling 10s before harness..."
Start-Sleep -Seconds 10

# --- run the harness ---------------------------------------------------------
if (-not (Test-Path $harness)) { Log "FATAL: harness not found at $harness"; return [pscustomobject]@{ ExitCode=99; Result='NO-HARNESS'; Installs=$results } }
Log "running UninstallE2E.exe (execute git,vscode)..."
$consoleLog = Join-Path $Output 'harness-console.log'
$proc = Start-Process -FilePath $harness `
    -ArgumentList "--output `"$Output`" --execute git,vscode --require 7zip,git,vscode,notepadpp --settleSeconds 90 --execTimeoutSeconds 120" `
    -Wait -PassThru -RedirectStandardOutput $consoleLog -RedirectStandardError (Join-Path $Output 'harness-stderr.log')
$rc = $proc.ExitCode
Log "harness exited rc=$rc"

$result = switch ($rc) { 0 {'PASS'} 3 {'GUARD-REFUSED'} default {"FAIL($rc)"} }
$rc | Set-Content -LiteralPath (Join-Path $Output 'harness-exitcode.txt')
$result | Set-Content -LiteralPath (Join-Path $Output 'uninstall-e2e-result.txt')
Log "RESULT: $result"

[pscustomobject]@{ ExitCode = $rc; Result = $result; Installs = $results }
