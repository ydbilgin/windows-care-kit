# populated-seed-guest-run.ps1 - runs INSIDE the WCK-E2E guest over PowerShell Direct
# (Windows PowerShell 5.1: stock Win11 has no pwsh 7). Keep this file 5.1-compatible:
# NO ternary (?:), NO null-conditional (?.), NO ??, NO #requires -Version 7.
# ASCII ONLY in string literals: a no-BOM UTF-8 file read by Windows PowerShell 5.1's default
# (system codepage) decoder mangles multi-byte characters like an em dash inside a double-quoted
# string into garbage tokens and breaks the parse - confirmed with a real 5.1 ParseFile check.
#
# Seeds the guest with real, obviously-synthetic content so the Backup / Clean / Install
# screenshots taken by Capture-PopulatedShots.ps1 show POPULATED state instead of empty:
#   - installs git/Notepad++/VS Code (+ Chrome, best-effort) from the cached offline
#     installers, then writes their user-profile config files (the same source paths the
#     bundled backup manifest already declares: %USERPROFILE%\.gitconfig,
#     %APPDATA%\Code\User, Notepad++'s config dir, Chrome's Preferences/Bookmarks) -
#     mirrors realmig-source-guest-run.ps1's seeding, without its evidence/nonce plumbing
#     (this is a presentation seed, not a security-evidence harness).
#   - creates realistic, personal-data-free junk: a handful of *.log/*.tmp files under
#     %TEMP% (and best-effort %WINDIR%\Temp), a synthetic Chrome-shaped cache directory
#     (Win32JunkProbe only checks the folder EXISTS - it does not require a real browser),
#     and a few files sent to the Recycle Bin (not permanently deleted) so the Clean
#     module's Recycle Bin section has real stats to show.
#
# No personal/real user data anywhere: every written value is a hardcoded WCK/demo string.
[CmdletBinding()]
param(
    [string] $InstallersDir = 'C:\WCK-Input\installers',
    [string] $Output = 'C:\WCK-Output',
    [switch] $SkipInstalls
)

$ErrorActionPreference = 'Continue'
$env:TEMP = "$env:LOCALAPPDATA\Temp"
$env:TMP = $env:TEMP

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$installResults = @()

function Log([string]$Message) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $Output 'populated-seed.log') -Value $line
}

function Ok-InstallExit([int]$Code) { return $Code -in @(0, 1641, 3010) }

function Install-One([string]$Name, [string]$File, [scriptblock]$Run) {
    $path = Join-Path $InstallersDir $File
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Log "missing installer (skipping): $File"
        $script:installResults += [pscustomobject]@{ name = $Name; file = $File; exitCode = $null; status = 'missing' }
        return $false
    }
    Log "installing $Name from $File"
    try {
        $code = [int](& $Run $path)
        $status = 'failed'
        if (Ok-InstallExit $code) { $status = 'ok' }
        $script:installResults += [pscustomobject]@{ name = $Name; file = $File; exitCode = $code; status = $status }
        return (Ok-InstallExit $code)
    } catch {
        $script:installResults += [pscustomobject]@{ name = $Name; file = $File; exitCode = $null; status = "exception: $($_.Exception.Message)" }
        return $false
    }
}

function Write-Text([string]$Path, [string]$Text) {
    $dir = Split-Path -Parent $Path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Set-Content -LiteralPath $Path -Value $Text -Encoding utf8
}

function Write-FillerFile([string]$Path, [int]$ApproxBytes) {
    $dir = Split-Path -Parent $Path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $line = "WCK synthetic demo filler - not real user data. This file exists only to give the Clean/Backup screenshots realistic non-zero sizes.`r`n"
    $need = [Math]::Max(1, [int]([Math]::Ceiling($ApproxBytes / [double]$line.Length)))
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $need; $i++) { [void]$sb.Append($line) }
    Set-Content -LiteralPath $Path -Value $sb.ToString() -Encoding utf8 -NoNewline
}

function Stop-AppProcesses {
    Get-Process -Name 'Code', 'Code - Insiders', 'chrome', 'notepad++' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# 1) Install real apps (best-effort - a missing installer or failed install never aborts
#    the seed; Backup/Install screens populate from the bundled manifest regardless of what
#    is actually on disk, so a partial install still leaves a usable demo).
# ---------------------------------------------------------------------------
if (-not $SkipInstalls) {
    Install-One 'Git' 'git.exe' { param($p) (Start-Process $p -ArgumentList '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /NOCANCEL /SP-' -Wait -PassThru).ExitCode } | Out-Null
    Install-One 'Notepad++' 'npp.exe' { param($p) (Start-Process $p -ArgumentList '/S' -Wait -PassThru).ExitCode } | Out-Null
    Install-One 'VS Code' 'vscode.exe' { param($p) (Start-Process $p -ArgumentList '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /MERGETASKS=!runcode' -Wait -PassThru).ExitCode } | Out-Null
    Install-One 'Google Chrome' 'chrome.msi' { param($p) (Start-Process msiexec.exe -ArgumentList "/i `"$p`" /qn /norestart" -Wait -PassThru).ExitCode } | Out-Null
    Stop-AppProcesses
} else {
    Log "SkipInstalls set - writing profile config only, no installers run"
}

# ---------------------------------------------------------------------------
# 2) Profile-config seed - the exact source paths the bundled backup manifest declares
#    (10-developer.json: gitconfig / vscode-user; 20-browser.json: chrome). All values are
#    hardcoded WCK/demo strings, never real user data.
# ---------------------------------------------------------------------------
$gitExe = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path -LiteralPath $gitExe -PathType Leaf)) {
    $gitCmd = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($gitCmd) { $gitExe = $gitCmd.Source } else { $gitExe = $null }
}
if ($gitExe) {
    & $gitExe config --global user.name 'WCK Demo' | Out-Null
    & $gitExe config --global user.email 'wck-demo@wck-e2e.invalid' | Out-Null
    Log "git config written -> $env:USERPROFILE\.gitconfig"
} else {
    Log "git not found; writing .gitconfig directly"
    Write-Text (Join-Path $env:USERPROFILE '.gitconfig') "[user]`r`n`tname = WCK Demo`r`n`temail = wck-demo@wck-e2e.invalid`r`n"
}

$nppDir = Join-Path $env:APPDATA 'Notepad++'
$nppConfig = Join-Path $nppDir 'config.xml'
$nppShortcuts = Join-Path $nppDir 'shortcuts.xml'
if (-not (Test-Path -LiteralPath $nppConfig -PathType Leaf)) {
    Write-Text $nppConfig '<NotepadPlus><GUIConfigs><GUIConfig name="WCK">demo</GUIConfig></GUIConfigs></NotepadPlus>'
}
if (-not (Test-Path -LiteralPath $nppShortcuts -PathType Leaf)) {
    Write-Text $nppShortcuts '<NotepadPlus><InternalCommands><Shortcut id="41002" Ctrl="yes" Alt="no" Shift="no" Key="83" /></InternalCommands></NotepadPlus>'
}
Log "Notepad++ config written -> $nppDir"

$codeDir = Join-Path $env:APPDATA 'Code\User'
$codeSettings = Join-Path $codeDir 'settings.json'
$codeKeys = Join-Path $codeDir 'keybindings.json'
Write-Text $codeSettings "{`n  `"window.zoomLevel`": 1,`n  `"wck.populatedShots.demo`": true`n}"
Write-Text $codeKeys "[`n  { `"key`": `"ctrl+alt+w`", `"command`": `"workbench.action.showCommands`" }`n]"
Log "VS Code user settings written -> $codeDir"

$chromeDefault = Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data\Default'
$chromePreferences = Join-Path $chromeDefault 'Preferences'
$chromeBookmarks = Join-Path $chromeDefault 'Bookmarks'
if (-not (Test-Path -LiteralPath $chromePreferences -PathType Leaf)) {
    Write-Text $chromePreferences '{ "profile": { "name": "WCK Demo" }, "wckPopulatedShotsDemo": true }'
}
if (-not (Test-Path -LiteralPath $chromeBookmarks -PathType Leaf)) {
    Write-Text $chromeBookmarks '{ "roots": { "bookmark_bar": { "children": [ { "name": "WCK", "type": "url", "url": "https://example.invalid" } ] } } }'
}
Log "Chrome profile files written -> $chromeDefault"

# ---------------------------------------------------------------------------
# 3) Junk seed for the Clean module. Win32JunkProbe reports a folder as soon as it EXISTS
#    (it does not require a real browser install), so the synthetic Chrome-shaped cache dirs
#    below are enough to make the "browser cache" candidates appear with a real, non-zero size.
# ---------------------------------------------------------------------------
$tempRoot = $env:TEMP
for ($i = 1; $i -le 4; $i++) {
    Write-FillerFile (Join-Path $tempRoot "wck-demo-$i.tmp") 65536
}
for ($i = 1; $i -le 3; $i++) {
    Write-FillerFile (Join-Path $tempRoot "wck-demo-$i.log") 32768
}
Log "wrote 7 synthetic *.tmp/*.log files under $tempRoot"

try {
    $winTemp = Join-Path $env:WINDIR 'Temp'
    Write-FillerFile (Join-Path $winTemp 'wck-demo-wintemp.tmp') 32768
    Log "wrote a synthetic file under $winTemp"
} catch {
    Log "could not write under Windows\Temp (non-fatal): $($_.Exception.Message)"
}

foreach ($cacheLeaf in @('Cache', 'Code Cache', 'GPUCache')) {
    $dir = Join-Path $chromeDefault $cacheLeaf
    for ($i = 1; $i -le 3; $i++) {
        Write-FillerFile (Join-Path $dir "data_$i") 16384
    }
}
Log "wrote synthetic Chrome-shaped cache folders under $chromeDefault"

# ---------------------------------------------------------------------------
# 4) Recycle Bin seed: write a few files then send them to the Recycle Bin (not a permanent
#    delete) via Microsoft.VisualBasic.FileIO - so Clean's "Recycle Bin" section has real,
#    non-zero stats if it is refreshed.
# ---------------------------------------------------------------------------
try {
    Add-Type -AssemblyName Microsoft.VisualBasic
    $recycleSrc = Join-Path $tempRoot 'wck-recycle-seed'
    New-Item -ItemType Directory -Force -Path $recycleSrc | Out-Null
    $recycled = 0
    for ($i = 1; $i -le 3; $i++) {
        $p = Join-Path $recycleSrc "wck-demo-recycled-$i.txt"
        Write-FillerFile $p 8192
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
            $p,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
        $recycled++
    }
    Remove-Item -LiteralPath $recycleSrc -Recurse -Force -ErrorAction SilentlyContinue
    Log "sent $recycled synthetic file(s) to the Recycle Bin"
} catch {
    Log "Recycle Bin seed failed (non-fatal): $($_.Exception.Message)"
}

$summary = [pscustomobject]@{
    generatedAt = (Get-Date).ToUniversalTime()
    machineName = $env:COMPUTERNAME
    installs = $installResults
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Output 'populated-seed.json') -Encoding utf8

Log "populated-seed-guest-run.ps1 done"
return [pscustomobject]@{ ExitCode = 0 }
