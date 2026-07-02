# Runs INSIDE the WCK-E2E source guest over PowerShell Direct (Windows PowerShell 5.1).
# Keep this script 5.1-compatible: NO ternary (?:), NO null-conditional (?.), NO #requires 7.
[CmdletBinding()]
param(
    [string] $InputDir = 'C:\WCK-Input',
    [string] $Output = 'C:\WCK-Output',
    [Parameter(Mandatory)] [string] $Nonce,
    [switch] $ChromeSeedFallback
)

$ErrorActionPreference = 'Continue'
$env:TEMP = "$env:LOCALAPPDATA\Temp"
$env:TMP = $env:TEMP

$installers = Join-Path $InputDir 'installers'
$harness = Join-Path $InputDir 'harness\MigrationRealRestore.exe'
$package = Join-Path $Output 'package'
New-Item -ItemType Directory -Force -Path $Output, $package | Out-Null

$installResults = @()
$seedRecords = @()

function Log([string]$Message) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $Output 'source-progress.log') -Value $line
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

function Write-Text([string]$Path, [string]$Text) {
    $dir = Split-Path -Parent $Path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Set-Content -LiteralPath $Path -Value $Text -Encoding utf8
}

function Get-FileSha([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Add-SeedRecord([string]$RecipeId, [string]$Path, [string]$SeedMode) {
    $script:seedRecords += [pscustomobject]@{
        recipeId = $RecipeId
        path = $Path
        seedMode = $SeedMode
        exists = (Test-Path -LiteralPath $Path -PathType Leaf)
        sha256 = Get-FileSha $Path
    }
}

$allInstallsOk = $true
$allInstallsOk = (Install-One 'Git' 'git.exe' { param($p) (Start-Process $p -ArgumentList '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /NOCANCEL /SP-' -Wait -PassThru).ExitCode }) -and $allInstallsOk
$allInstallsOk = (Install-One 'Notepad++' 'npp.exe' { param($p) (Start-Process $p -ArgumentList '/S' -Wait -PassThru).ExitCode }) -and $allInstallsOk
$allInstallsOk = (Install-One 'VS Code' 'vscode.exe' { param($p) (Start-Process $p -ArgumentList '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /MERGETASKS=!runcode' -Wait -PassThru).ExitCode }) -and $allInstallsOk
$chromeInstalled = $false
if (-not $ChromeSeedFallback) {
    $chromeInstalled = Install-One 'Google Chrome' 'chrome.msi' { param($p) (Start-Process msiexec.exe -ArgumentList "/i `"$p`" /qn /norestart" -Wait -PassThru).ExitCode } -Optional
}
Stop-AppProcesses

$gitExe = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path -LiteralPath $gitExe -PathType Leaf)) {
    $gitCmd = Get-Command git.exe -ErrorAction SilentlyContinue
    $gitExe = if ($gitCmd) { $gitCmd.Source } else { $null }
}
if (-not $gitExe) { throw "git executable not found after install" }
& $gitExe config --global user.name 'WCK Source' | Out-Null
& $gitExe config --global user.email 'wck-source@wck-e2e.invalid' | Out-Null
Add-SeedRecord 'git.config' (Join-Path $env:USERPROFILE '.gitconfig') 'real-app'

$nppMode = 'script-written'
$nppExe = Join-Path $env:ProgramFiles 'Notepad++\notepad++.exe'
if (Test-Path -LiteralPath $nppExe -PathType Leaf) {
    try {
        $p = Start-Process -FilePath $nppExe -PassThru
        Start-Sleep -Seconds 5
        $null = $p.CloseMainWindow()
        Start-Sleep -Seconds 2
        Stop-AppProcesses
        $nppMode = 'real-app'
    } catch {
        Stop-AppProcesses
    }
}
$nppDir = Join-Path $env:APPDATA 'Notepad++'
$nppConfig = Join-Path $nppDir 'config.xml'
$nppShortcuts = Join-Path $nppDir 'shortcuts.xml'
if (-not (Test-Path -LiteralPath $nppConfig -PathType Leaf)) { Write-Text $nppConfig '<NotepadPlus><GUIConfigs><GUIConfig name="WCK">source</GUIConfig></GUIConfigs></NotepadPlus>' ; $nppMode = 'script-written' }
if (-not (Test-Path -LiteralPath $nppShortcuts -PathType Leaf)) { Write-Text $nppShortcuts '<NotepadPlus><InternalCommands><Shortcut id="41002" Ctrl="yes" Alt="no" Shift="no" Key="83" /></InternalCommands></NotepadPlus>' ; $nppMode = 'script-written' }
Add-SeedRecord 'notepadplusplus.notepadplusplus' $nppConfig $nppMode
Add-SeedRecord 'notepadplusplus.notepadplusplus' $nppShortcuts $nppMode

$codeDir = Join-Path $env:APPDATA 'Code\User'
$codeSettings = Join-Path $codeDir 'settings.json'
$codeKeys = Join-Path $codeDir 'keybindings.json'
$snippets = Join-Path $codeDir 'snippets'
Write-Text $codeSettings "{`n  `"window.zoomLevel`": 1,`n  `"wck.realmig.source`": true`n}"
Write-Text $codeKeys "[`n  { `"key`": `"ctrl+alt+w`", `"command`": `"workbench.action.showCommands`" }`n]"
New-Item -ItemType Directory -Force -Path $snippets | Out-Null
Write-Text (Join-Path $snippets 'id_rsa') ('FAKE ' + 'PRIVATE ' + 'KEY')
Write-Text (Join-Path $snippets 'app.secret') ('FAKE APP ' + 'SECRET TOKEN')
Add-SeedRecord 'microsoft.vscode' $codeSettings 'script-written'
Add-SeedRecord 'microsoft.vscode' $codeKeys 'script-written'

$chromeMode = if ($ChromeSeedFallback) { 'script-written-fallback' } else { 'script-written' }
$chromeExe = Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'
if (-not (Test-Path -LiteralPath $chromeExe -PathType Leaf)) { $chromeExe = Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe' }
if ($chromeInstalled -and (Test-Path -LiteralPath $chromeExe -PathType Leaf)) {
    try {
        $p = Start-Process -FilePath $chromeExe -ArgumentList '--no-first-run about:blank' -PassThru
        Start-Sleep -Seconds 8
        Stop-AppProcesses
        $chromeMode = 'real-app'
    } catch {
        Stop-AppProcesses
    }
}
$chromeDefault = Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data\Default'
$chromePreferences = Join-Path $chromeDefault 'Preferences'
$chromeBookmarks = Join-Path $chromeDefault 'Bookmarks'
if (-not (Test-Path -LiteralPath $chromePreferences -PathType Leaf)) { Write-Text $chromePreferences '{ "profile": { "name": "WCK Source" }, "wckRealmig": true }'; if ($chromeMode -eq 'real-app') { $chromeMode = 'script-written' } }
if (-not (Test-Path -LiteralPath $chromeBookmarks -PathType Leaf)) { Write-Text $chromeBookmarks '{ "roots": { "bookmark_bar": { "children": [ { "name": "WCK", "type": "url", "url": "https://example.invalid" } ] } } }' }
Add-SeedRecord 'google.chrome' $chromePreferences $chromeMode
Add-SeedRecord 'google.chrome' $chromeBookmarks $chromeMode

Stop-AppProcesses

$sourceSeed = [pscustomobject]@{
    pass = $allInstallsOk
    mode = 'sourceSeed'
    generatedAt = (Get-Date).ToUniversalTime()
    machineName = $env:COMPUTERNAME
    profileRoot = $env:USERPROFILE
    chromeSeedFallback = [bool]$ChromeSeedFallback
    installs = $installResults
    seeds = $seedRecords
}
$sourceSeed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Output 'sourceSeed.json') -Encoding utf8

if (-not (Test-Path -LiteralPath $harness -PathType Leaf)) {
    Log "FATAL: harness not found at $harness"
    return [pscustomobject]@{ ExitCode = 99; Result = 'NO-HARNESS' }
}

Log "running MigrationRealRestore backup"
$console = & $harness `
    --mode backup `
    --package $package `
    --output $Output `
    --recipes 'git.config,microsoft.vscode,notepadplusplus.notepadplusplus,google.chrome' `
    --assert-pruned 'id_rsa,app.secret' `
    --require-guest-nonce $Nonce `
    --expect-profile-root $env:USERPROFILE 2>&1 | Out-String
$rc = $LASTEXITCODE
$console | Set-Content -LiteralPath (Join-Path $Output 'realmig-backup-console.txt') -Encoding utf8
Log "backup exit: $rc"

if (-not $allInstallsOk -and $rc -eq 0) { $rc = 1 }
return [pscustomobject]@{ ExitCode = $rc; Result = $(if ($rc -eq 0) { 'PASS' } else { "FAIL($rc)" }); Installs = $installResults }
