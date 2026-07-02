# Runs INSIDE the WCK-E2E-2 dest guest over PowerShell Direct (Windows PowerShell 5.1).
# Keep this script 5.1-compatible: NO ternary (?:), NO null-conditional (?.), NO #requires 7.
[CmdletBinding()]
param(
    [string] $InputDir = 'C:\WCK-Input',
    [string] $Output = 'C:\WCK-Output',
    [Parameter(Mandatory)] [string] $Nonce,
    [string] $ExpectGitEmail = 'wck-source@wck-e2e.invalid'
)

$ErrorActionPreference = 'Continue'
$env:TEMP = "$env:LOCALAPPDATA\Temp"
$env:TMP = $env:TEMP

$installers = Join-Path $InputDir 'installers'
$harness = Join-Path $InputDir 'harness\MigrationRealRestore.exe'
$package = Join-Path $InputDir 'package'
$state = 'C:\WCK-RealMig\state'
New-Item -ItemType Directory -Force -Path $Output, $state | Out-Null

$installResults = @()

function Log([string]$Message) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $Output 'dest-progress.log') -Value $line
}

function Ok-InstallExit([int]$Code) { return $Code -in @(0, 1641, 3010) }

function Install-One([string]$Name, [string]$File, [scriptblock]$Run, [switch]$Optional) {
    $path = Join-Path $installers $File
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Log "missing installer: $File"
        $script:installResults += [pscustomobject]@{ name = $Name; file = $File; exitCode = $null; status = $(if ($Optional) { 'missing-optional' } else { 'missing' }) }
        return $Optional.IsPresent
    }
    Log "installing $Name from $File"
    try {
        $code = [int](& $Run $path)
        $status = if (Ok-InstallExit $code) { 'ok' } else { 'failed' }
        $script:installResults += [pscustomobject]@{ name = $Name; file = $File; exitCode = $code; status = $status }
        return (Ok-InstallExit $code)
    } catch {
        $script:installResults += [pscustomobject]@{ name = $Name; file = $File; exitCode = $null; status = "exception: $($_.Exception.Message)" }
        return $false
    }
}

function Stop-AppProcesses {
    Get-Process -Name 'Code','Code - Insiders','chrome','notepad++' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Run-Harness([string]$Mode, [string[]]$HarnessArgs) {
    Log "running MigrationRealRestore $Mode"
    $consolePath = Join-Path $Output "realmig-$Mode-console.txt"
    $console = & $harness @HarnessArgs 2>&1 | Out-String
    $rc = $LASTEXITCODE
    $console | Set-Content -LiteralPath $consolePath -Encoding utf8
    Log "$Mode exit: $rc"
    return $rc
}

$allInstallsOk = $true
$allInstallsOk = (Install-One 'Git' 'git.exe' { param($p) (Start-Process $p -ArgumentList '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /NOCANCEL /SP-' -Wait -PassThru).ExitCode }) -and $allInstallsOk
$allInstallsOk = (Install-One 'Notepad++' 'npp.exe' { param($p) (Start-Process $p -ArgumentList '/S' -Wait -PassThru).ExitCode }) -and $allInstallsOk
$allInstallsOk = (Install-One 'VS Code' 'vscode.exe' { param($p) (Start-Process $p -ArgumentList '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /MERGETASKS=!runcode' -Wait -PassThru).ExitCode }) -and $allInstallsOk
$null = Install-One 'Google Chrome' 'chrome.msi' { param($p) (Start-Process msiexec.exe -ArgumentList "/i `"$p`" /qn /norestart" -Wait -PassThru).ExitCode } -Optional

$nppExe = Join-Path $env:ProgramFiles 'Notepad++\notepad++.exe'
if (Test-Path -LiteralPath $nppExe -PathType Leaf) {
    try {
        $p = Start-Process -FilePath $nppExe -PassThru
        Start-Sleep -Seconds 5
        $null = $p.CloseMainWindow()
    } catch { }
}

$chromeExe = Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'
if (-not (Test-Path -LiteralPath $chromeExe -PathType Leaf)) { $chromeExe = Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe' }
if (Test-Path -LiteralPath $chromeExe -PathType Leaf) {
    try {
        $p = Start-Process -FilePath $chromeExe -ArgumentList '--no-first-run about:blank' -PassThru
        Start-Sleep -Seconds 8
    } catch { }
}
Stop-AppProcesses

if (-not (Test-Path -LiteralPath $harness -PathType Leaf)) {
    Log "FATAL: harness not found at $harness"
    return [pscustomobject]@{ ExitCode = 99; Result = 'NO-HARNESS' }
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'migration-manifest.json') -PathType Leaf)) {
    Log "FATAL: package manifest not found under $package"
    return [pscustomobject]@{ ExitCode = 98; Result = 'NO-PACKAGE' }
}

$common = @('--require-guest-nonce', $Nonce, '--expect-profile-root', $env:USERPROFILE, '--output', $Output)

$fingerprintArgs = @('--mode', 'fingerprint', '--package', $package) + $common
$rc1 = Run-Harness 'fingerprint' $fingerprintArgs

Stop-AppProcesses
$restoreArgs = @('--mode', 'restore', '--package', $package, '--state', $state) + $common
$rc2 = if ($rc1 -eq 0) { Run-Harness 'restore' $restoreArgs } else { 1 }

$baseline = Join-Path $Output 'realmig-fingerprint-evidence.json'
$verifyArgs = @('--mode', 'verify', '--package', $package, '--state', $state, '--baseline', $baseline, '--expect-git-email', $ExpectGitEmail) + $common
$rc3 = if ($rc2 -eq 0) { Run-Harness 'verify' $verifyArgs } else { 1 }

Stop-AppProcesses
$undoArgs = @('--mode', 'undo', '--state', $state) + $common
$rc4 = if ($rc3 -eq 0) { Run-Harness 'undo' $undoArgs } else { 1 }

$installJson = [pscustomobject]@{
    pass = $allInstallsOk
    mode = 'destProvision'
    generatedAt = (Get-Date).ToUniversalTime()
    machineName = $env:COMPUTERNAME
    profileRoot = $env:USERPROFILE
    installs = $installResults
}
$installJson | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Output 'destProvision.json') -Encoding utf8

$exit = if (($rc1 -eq 0) -and ($rc2 -eq 0) -and ($rc3 -eq 0) -and ($rc4 -eq 0) -and $allInstallsOk) { 0 } else { 1 }
return [pscustomobject]@{
    ExitCode = $exit
    Result = $(if ($exit -eq 0) { 'PASS' } else { "FAIL($exit)" })
    Fingerprint = $rc1
    Restore = $rc2
    Verify = $rc3
    Undo = $rc4
    Installs = $installResults
}
