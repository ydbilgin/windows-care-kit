#requires -Version 7.0
<#
.SYNOPSIS
    Windows Care Kit (WCK) · Step 4 host-side stage builder.

.DESCRIPTION
    Builds a fully OFFLINE, self-contained test bundle for the throwaway Windows
    Sandbox defined by WindowsCareKit-step4-test.wsb. Run this on the HOST.
    No admin rights needed.

    It produces (under -StagingRoot, default C:\WCK-SandboxStaging):
      repo\        clean source copy (no bin/obj/.git) + an offline nuget.config
      dotnet\      portable copy of the per-user .NET SDK (so the VM needs no SDK)
      localfeed\   a flat .nupkg feed with the exact dependency closure
    and an empty -OutputRoot (default C:\WCK-SandboxOutput) where the VM drops
    results (step4.trx, step4-console.log, step4-exitcode.txt).

    Because the bundle carries its own SDK + NuGet feed, the sandbox runs with
    networking DISABLED. Nothing here enables any Windows feature or reboots.

.NOTES
    Prereq to RUN the produced bundle: Windows Sandbox must be enabled once
    (admin, then reboot):
        Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
    That is a deliberate, user-performed, reversible step — this script does NOT do it.
#>
[CmdletBinding()]
param(
    [string]$StagingRoot = 'C:\WCK-SandboxStaging',
    [string]$OutputRoot  = 'C:\WCK-SandboxOutput',
    [switch]$RefreshSdk,      # re-copy the ~0.7 GB SDK (default: reuse if already staged)
    [switch]$SkipHostTest     # skip the host-side green-baseline check (Category!=Destructive)
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# --- locate repo + the per-user .NET SDK ------------------------------------
$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$DotnetRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$DotnetExe  = Join-Path $DotnetRoot 'dotnet.exe'
$Slnx       = Join-Path $RepoRoot 'WindowsCareKit.slnx'

if (-not (Test-Path $DotnetExe)) {
    throw "Per-user .NET SDK not found at '$DotnetExe'. Open a shell where 'dotnet --version' reports 10.x first."
}
$env:DOTNET_ROOT = $DotnetRoot
$env:PATH = "$DotnetRoot;$env:PATH"
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Write-Step "Repo:   $RepoRoot"
Write-Step "SDK:    $DotnetRoot  ($(& $DotnetExe --version))"

# --- 0. host green baseline (host-safe, excludes destructive) ----------------
if (-not $SkipHostTest) {
    Write-Step 'Host green-baseline: build + test (Category!=Destructive)...'
    & $DotnetExe build $Slnx -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Host build failed (exit $LASTEXITCODE) — fix before staging." }
    & $DotnetExe test (Join-Path $RepoRoot 'tests\Suite.Tests\Suite.Tests.csproj') `
        -c Debug --no-build --filter 'Category!=Destructive' -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Host tests failed (exit $LASTEXITCODE) — fix before staging." }
} else {
    Write-Step 'Skipping host green-baseline (-SkipHostTest).'
}

# --- staging skeleton --------------------------------------------------------
$StageRepo  = Join-Path $StagingRoot 'repo'
$StageDotnet= Join-Path $StagingRoot 'dotnet'
$StageFeed  = Join-Path $StagingRoot 'localfeed'
$TmpGp      = Join-Path $StagingRoot '_gp'

New-Item -ItemType Directory -Force -Path $StagingRoot, $StageFeed | Out-Null

# --- 1. portable SDK copy (reuse unless -RefreshSdk) -------------------------
if ($RefreshSdk -or -not (Test-Path (Join-Path $StageDotnet 'dotnet.exe'))) {
    Write-Step "Copying portable SDK -> $StageDotnet  (~0.7 GB, one-time)..."
    robocopy $DotnetRoot $StageDotnet /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy SDK failed (code $LASTEXITCODE)." }
} else {
    Write-Step 'Portable SDK already staged (use -RefreshSdk to re-copy).'
}

# --- 2. flat OFFLINE NuGet feed (exact dependency closure) -------------------
Write-Step 'Restoring dependency closure into a flat offline feed...'
if (Test-Path $TmpGp) { Remove-Item $TmpGp -Recurse -Force }
& $DotnetExe restore $Slnx --packages $TmpGp | Out-Null
if ($LASTEXITCODE -ne 0) { throw "restore failed (exit $LASTEXITCODE)." }
if (Test-Path $StageFeed) { Remove-Item $StageFeed -Recurse -Force }
New-Item -ItemType Directory -Force -Path $StageFeed | Out-Null
$nupkgs = Get-ChildItem $TmpGp -Recurse -Filter *.nupkg -File
foreach ($p in $nupkgs) { Copy-Item $p.FullName (Join-Path $StageFeed $p.Name) -Force }
Remove-Item $TmpGp -Recurse -Force
Write-Step "Offline feed: $($nupkgs.Count) packages -> $StageFeed"

# --- 3. clean source copy (no bin/obj/.git/orchestration/personal) ----------
Write-Step "Staging clean source -> $StageRepo ..."
$excludeDirs = @(
    (Join-Path $RepoRoot '.git'), (Join-Path $RepoRoot '.vs'),
    (Join-Path $RepoRoot '.planning'), (Join-Path $RepoRoot '.cx_dispatch'),
    (Join-Path $RepoRoot '.ax_dispatch'), (Join-Path $RepoRoot '.cx_dispatch_locks'),
    (Join-Path $RepoRoot 'payload'), (Join-Path $RepoRoot 'legacy'),
    (Join-Path $RepoRoot 'publish'), (Join-Path $RepoRoot 'artifacts'),
    'bin', 'obj', 'TestResults'
)
$rc = @($StageRepo, '/MIR', '/XD') + $excludeDirs + @('/XF', '*.user', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
robocopy $RepoRoot @rc | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy source failed (code $LASTEXITCODE)." }

# --- 4. offline nuget.config in the STAGED repo (clears online sources) ------
# Points restore at the flat feed via the path it will have INSIDE the sandbox.
$nugetConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by step4-stage.ps1 for the offline Step 4 sandbox. Not in the real repo. -->
<configuration>
  <packageSources>
    <clear />
    <add key="wck-offline" value="C:\WCK-Input\localfeed" />
  </packageSources>
</configuration>
'@
Set-Content -Path (Join-Path $StageRepo 'nuget.config') -Value $nugetConfig -Encoding UTF8

# --- 5. reset output folder --------------------------------------------------
if (Test-Path $OutputRoot) { Remove-Item $OutputRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host ' Step 4 bundle ready.' -ForegroundColor Green
Write-Host "   staging : $StagingRoot" -ForegroundColor Green
Write-Host "   output  : $OutputRoot" -ForegroundColor Green
Write-Host '------------------------------------------------------------' -ForegroundColor Green
Write-Host ' NEXT: double-click  sandbox\WindowsCareKit-step4-test.wsb'   -ForegroundColor Green
Write-Host '       (requires Windows Sandbox enabled once: admin +'        -ForegroundColor Green
Write-Host '        Enable-WindowsOptionalFeature -Online -FeatureName'    -ForegroundColor Green
Write-Host '        Containers-DisposableClientVM -All  + reboot)'         -ForegroundColor Green
Write-Host " Results return to: $OutputRoot\step4.trx + step4-console.log" -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green
