#requires -Version 7.0
<#
.SYNOPSIS
    Windows Care Kit (WCK) · Uninstall E2E host-side stage builder.

.DESCRIPTION
    HOST-SAFE stage script.  Does NOT install or uninstall any real program, does
    NOT open any window, and does NOT touch the real registry / profile / programs.

    It:
      1. Builds (dotnet build Debug) the whole solution to verify 0 errors.
      2. Publishes the UninstallE2E console harness SELF-CONTAINED win-x64 to
         -StagingDir\harness (the clean Sandbox VM has NO .NET runtime, so the
         harness must carry its own).
      3. Copies the sandbox scripts (CRLF-normalized) into -StagingDir\scripts so
         the VM can find them.
      4. Prepares an empty -OutputDir for evidence coming back from the VM.

    The sandbox VM (uninstall-e2e.wsb + uninstall-e2e-run.cmd) handles everything
    that requires real installs / uninstalls.  This script only compiles + stages.

.PARAMETER StagingDir
    Host directory to place the published harness.  Default: C:\WCK-UninstallStaging.

.PARAMETER OutputDir
    Host directory the VM writes evidence into.  Default: C:\WCK-UninstallOutput.

.PARAMETER SkipBuild
    Skip the solution build (useful if you just built).

.NOTES
    Uses the per-user .NET 10 SDK at $env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe.
#>
[CmdletBinding()]
param(
    [string] $StagingDir = 'C:\WCK-UninstallStaging',
    [string] $OutputDir  = 'C:\WCK-UninstallOutput',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# --- locate per-user .NET 10 SDK ---
$DotnetRoot = "$env:LOCALAPPDATA\Microsoft\dotnet"
$DotnetExe  = "$DotnetRoot\dotnet.exe"
if (-not (Test-Path $DotnetExe)) {
    throw "Per-user .NET SDK not found at '$DotnetExe'.  Run this in a shell where 'dotnet --version' reports 10.x."
}
$env:DOTNET_ROOT = $DotnetRoot
$env:PATH        = "$DotnetRoot;$env:PATH"
$env:DOTNET_NOLOGO                  = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT    = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$RepoRoot      = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Slnx          = Join-Path $RepoRoot 'WindowsCareKit.slnx'
$HarnessCsproj = Join-Path $RepoRoot 'tools\UninstallE2E\UninstallE2E.csproj'

Write-Step "Repo      : $RepoRoot"
Write-Step "SDK       : $DotnetRoot  ($(& $DotnetExe --version))"
Write-Step "StagingDir: $StagingDir"
Write-Step "OutputDir : $OutputDir"

# --- 1. Build solution ---
if (-not $SkipBuild) {
    Write-Step 'Building whole solution (Debug, 0 errors required)...'
    & $DotnetExe build $Slnx -c Debug
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build FAILED (exit $LASTEXITCODE).  Fix before staging."
    }
    Write-Step 'Build: OK'
} else {
    Write-Step 'Skipping build (-SkipBuild).'
}

# --- 2. Publish harness (SELF-CONTAINED win-x64; the clean Sandbox VM has NO .NET runtime) ---
$PublishDir = Join-Path $StagingDir 'harness'
Write-Step "Publishing UninstallE2E harness (self-contained win-x64) -> $PublishDir ..."
& $DotnetExe publish $HarnessCsproj -c Debug `
    --output $PublishDir `
    --runtime win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) {
    throw "Publish FAILED (exit $LASTEXITCODE)."
}
Write-Step "Publish: OK  ($PublishDir)"

# --- 3. Copy sandbox scripts so the VM can find them (CRLF-normalized) ---
# cmd.exe REQUIRES CRLF; with LF-only endings it loses line-sync on goto/( ) blocks
# and runs REM text as commands. .gitattributes keeps the checked-out copy CRLF; we
# ALSO normalize the staged copy here so the sandbox always gets CRLF.
$ScriptsDir = Join-Path $StagingDir 'scripts'
New-Item -ItemType Directory -Force -Path $ScriptsDir | Out-Null
$RunCmdSrc  = Join-Path $RepoRoot 'sandbox\uninstall-e2e-run.cmd'
$RunCmdDst  = Join-Path $ScriptsDir 'uninstall-e2e-run.cmd'
# Byte-exact LF->CRLF: insert 0x0D before every 0x0A that lacks one (content-preserving).
$srcBytes = [System.IO.File]::ReadAllBytes($RunCmdSrc)
$dst = [System.Collections.Generic.List[byte]]::new($srcBytes.Length + 256)
$prev = 0
foreach ($b in $srcBytes) {
    if ($b -eq 0x0A -and $prev -ne 0x0D) { $dst.Add([byte]0x0D); $dst.Add([byte]0x0A) }
    else { $dst.Add($b) }
    $prev = $b
}
[System.IO.File]::WriteAllBytes($RunCmdDst, $dst.ToArray())
Write-Step "Scripts staged (CRLF-normalized) -> $ScriptsDir"

# --- 4. Prepare output dir ---
# Guard against accidentally clearing an arbitrary directory. We recursively delete ONLY the default
# output dir (always safe to recreate) or an EMPTY dir. A non-default, non-empty path — even one that
# matches *WCK*Output* — is refused so a user-supplied folder with real files is never wiped (audit MINOR-1).
if (Test-Path $OutputDir) {
    $isDefault = ($OutputDir -eq 'C:\WCK-UninstallOutput')
    $isEmpty   = (-not (Get-ChildItem -LiteralPath $OutputDir -ErrorAction SilentlyContinue | Select-Object -First 1))
    if (-not ($isDefault -or $isEmpty)) {
        throw "OutputDir '$OutputDir' already exists, is non-empty, and is not the default 'C:\WCK-UninstallOutput'. Clear it manually before staging, or choose a different path."
    }
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Write-Step "Output dir prepared: $OutputDir"

Write-Host ''
Write-Host '================================================================' -ForegroundColor Green
Write-Host ' Uninstall E2E staging complete.' -ForegroundColor Green
Write-Host "   staging : $StagingDir" -ForegroundColor Green
Write-Host "   output  : $OutputDir" -ForegroundColor Green
Write-Host '----------------------------------------------------------------' -ForegroundColor Green
Write-Host ' NEXT: open sandbox\uninstall-e2e.wsb (human) or -auto.wsb (autonomous)' -ForegroundColor Green
Write-Host "   Evidence returns to: $OutputDir" -ForegroundColor Green
Write-Host '================================================================' -ForegroundColor Green
