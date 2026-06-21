#requires -Version 7.0
<#
.SYNOPSIS
    Windows Care Kit (WCK) · Migration E2E host-side stage builder.

.DESCRIPTION
    HOST-SAFE stage script.  Does NOT install any real program, does NOT open
    any window, does NOT touch real %UserProfile% / %AppData% / registry.

    It:
      1. Builds (dotnet build Debug) the whole solution to verify 0 errors.
      2. Publishes the MigrationE2E console harness (framework-dependent, Debug)
         to -StagingDir.
      3. Copies the sandbox scripts into -StagingDir so the VM can find them.
      4. Prepares an empty -OutputDir for evidence coming back from the VM.

    The sandbox VM (migration-e2e.wsb + migration-e2e-run.cmd) handles everything
    that requires real apps / real profile data.  This script only compiles + stages.

.PARAMETER StagingDir
    Host directory to place the published harness.  Default: C:\WCK-MigrationStaging.

.PARAMETER OutputDir
    Host directory the VM writes evidence into.  Default: C:\WCK-MigrationOutput.

.PARAMETER SkipBuild
    Skip the solution build (useful if you just built).

.NOTES
    Uses the per-user .NET 10 SDK:
        $env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe
    The caller must have that SDK on PATH or it is resolved automatically here.
#>
[CmdletBinding()]
param(
    [string] $StagingDir = 'C:\WCK-MigrationStaging',
    [string] $OutputDir  = 'C:\WCK-MigrationOutput',
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

$RepoRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Slnx      = Join-Path $RepoRoot 'WindowsCareKit.slnx'
$HarnessCsproj = Join-Path $RepoRoot 'tools\MigrationE2E\MigrationE2E.csproj'

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

# --- 2. Publish harness (SELF-CONTAINED win-x64; the clean Sandbox VM has NO .NET runtime,
#        so the harness must carry its own — a framework-dependent publish would fail to launch). ---
$PublishDir = Join-Path $StagingDir 'harness'
Write-Step "Publishing MigrationE2E harness (self-contained win-x64) -> $PublishDir ..."
& $DotnetExe publish $HarnessCsproj -c Debug `
    --output $PublishDir `
    --runtime win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) {
    throw "Publish FAILED (exit $LASTEXITCODE)."
}
Write-Step "Publish: OK  ($PublishDir)"

# --- 3. Copy sandbox scripts so the VM can find them ---
$ScriptsDir = Join-Path $StagingDir 'scripts'
New-Item -ItemType Directory -Force -Path $ScriptsDir | Out-Null
$SandboxDir = Join-Path $RepoRoot 'sandbox'
Copy-Item (Join-Path $SandboxDir 'migration-e2e-run.cmd') $ScriptsDir -Force

Write-Step "Scripts staged -> $ScriptsDir"

# --- 4. Prepare output dir ---
# FIX 6: guard against accidentally deleting an arbitrary directory.
# Only clear OutputDir if it matches the default pattern or is obviously a WCK output dir.
if (Test-Path $OutputDir) {
    $isDefault    = ($OutputDir -eq 'C:\WCK-MigrationOutput')
    $isWckPattern = ($OutputDir -like '*WCK*Output*')
    $isEmpty      = (-not (Get-ChildItem -LiteralPath $OutputDir -ErrorAction SilentlyContinue | Select-Object -First 1))

    if (-not ($isDefault -or $isWckPattern -or $isEmpty)) {
        throw "OutputDir '$OutputDir' already exists and does not match '*WCK*Output*' or the default 'C:\WCK-MigrationOutput'. Clear it manually before staging, or choose a different path."
    }
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Write-Step "Output dir prepared: $OutputDir"

Write-Host ''
Write-Host '================================================================' -ForegroundColor Green
Write-Host ' Migration E2E staging complete.' -ForegroundColor Green
Write-Host "   staging : $StagingDir" -ForegroundColor Green
Write-Host "   output  : $OutputDir" -ForegroundColor Green
Write-Host '----------------------------------------------------------------' -ForegroundColor Green
Write-Host ' NEXT: open sandbox\migration-e2e.wsb' -ForegroundColor Green
Write-Host "   Evidence returns to: $OutputDir" -ForegroundColor Green
Write-Host '================================================================' -ForegroundColor Green
