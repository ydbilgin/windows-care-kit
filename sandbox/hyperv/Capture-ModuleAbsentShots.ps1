#requires -Version 7.0
<#
.SYNOPSIS
  M4 payoff proof: capture the app running with a module physically ABSENT from disk. Publishes the
  self-contained folder, stages a copy, deletes Modules\migration\ recursively, verifies no
  Suite.Module.Migration*.dll survives anywhere in the staged tree, then captures three shots from the
  disposable WCK-E2E guest. Headless (no host window) — runs only in the VM.
.DESCRIPTION
  Mirrors Capture-ThemeShots.ps1 (same publish + guest-deploy + Show-InGuestApp plumbing) but proves the
  runtime folder-discovery loader: an install missing a module's folder simply lacks that module, with no
  crash. Shots:
    01-absent-bare               bare `--lang en`            -> app starts; nav shows 6 items, NO Migration tab
    02-absent-screen-migration   `--screen migration`        -> starts; deep-link ignored (default tab + first-run modal)
    03-absent-screen-backup      `--screen backup`           -> a still-present module renders normally

  The guest is a disposable, network-isolated eval VM; the autologon password is the documented throwaway
  from Install-CaptureAgent.ps1 (no value outside the VM). Exits non-zero if the exe dies or any shot is missing.
.EXAMPLE
  pwsh -File Capture-ModuleAbsentShots.ps1
#>
[CmdletBinding()]
param(
    [string] $VMName     = 'WCK-E2E',
    [string] $GuestUser  = 'wck',
    [string] $GuestPass  = 'WckE2E!2026',   # documented throwaway for the isolated guest (no value outside the VM)
    [string] $OutDir     = 'F:\WCK-VM\shots\module-absent',
    [string] $GuestApp   = 'C:\WCK-App',
    [int]    $SettleMs   = 2600,             # generous: async LoadAsync / scan populate
    [switch] $SkipPublish
)
$ErrorActionPreference = 'Stop'
function Step($m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "    $m" -ForegroundColor DarkGray }

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$csproj     = Join-Path $repoRoot 'src\Suite.App.Wpf\Suite.App.Wpf.csproj'
$publishDir = 'F:\WCK-VM\wck-app\publish'
$stagingDir = 'F:\WCK-VM\wck-app\publish-nomigration'
$dotnet     = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# Shot key (screen arg or $null for a bare launch) -> NN-ordered filename stem.
$shots = @(
    [pscustomobject]@{ Screen = $null;        File = '01-absent-bare.png' }
    [pscustomobject]@{ Screen = 'migration';  File = '02-absent-screen-migration.png' }
    [pscustomobject]@{ Screen = 'backup';      File = '03-absent-screen-backup.png' }
)

if (-not $SkipPublish) {
    Step "Publishing WPF app (self-contained folder win-x64)..."
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    & $dotnet publish $csproj -c Release --runtime win-x64 --self-contained true `
        --output $publishDir 2>&1 | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
    Remove-Item (Join-Path $publishDir '*.pdb') -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path (Join-Path $publishDir 'WindowsCareKit.exe'))) {
    throw "published exe not found in $publishDir (run without -SkipPublish)"
}

Step "Staging a copy with the Migration module removed..."
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $stagingDir -Recurse -Force

$migDir = Join-Path $stagingDir 'Modules\migration'
if (Test-Path $migDir) { Remove-Item $migDir -Recurse -Force }

# Honesty gate: "not on disk" must be literal — no Migration DLL may survive anywhere in the staged tree.
$survivors = Get-ChildItem -Path $stagingDir -Recurse -Filter 'Suite.Module.Migration*.dll' -ErrorAction SilentlyContinue
if ($survivors) {
    $survivors | ForEach-Object { Write-Host "    LEAK: $($_.FullName)" -ForegroundColor Red }
    throw "Migration DLL(s) survived in the staged tree — module removal is not literal."
}
Info "verified: zero Suite.Module.Migration*.dll under $stagingDir"

Step "Ensuring capture agent in guest (starts VM if needed)..."
& (Join-Path $PSScriptRoot 'Install-CaptureAgent.ps1') -VMName $VMName -GuestUser $GuestUser -GuestPass $GuestPass

$sec  = ConvertTo-SecureString $GuestPass -AsPlainText -Force
$cred = [System.Management.Automation.PSCredential]::new(".\$GuestUser", $sec)

Step "Deploying the Migration-absent build to guest $GuestApp ..."
$session = New-PSSession -VMName $VMName -Credential $cred
try {
    Invoke-Command -Session $session -ArgumentList $GuestApp -ScriptBlock {
        param($dst)
        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
    }
    Copy-Item -Path (Join-Path $stagingDir '*') -Destination $GuestApp -ToSession $session -Recurse -Force
    $deployed = Invoke-Command -Session $session -ArgumentList $GuestApp -ScriptBlock {
        param($d)
        [pscustomobject]@{
            Exe = Test-Path (Join-Path $d 'WindowsCareKit.exe')
            MigrationGone = -not (Get-ChildItem -Path $d -Recurse -Filter 'Suite.Module.Migration*.dll' -ErrorAction SilentlyContinue)
        }
    }
    if (-not $deployed.Exe) { throw "deploy verification failed: WindowsCareKit.exe missing in guest" }
    if (-not $deployed.MigrationGone) { throw "deploy verification failed: a Migration DLL is present in the guest tree" }
} finally { Remove-PSSession $session -EA SilentlyContinue }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exeGuest = Join-Path $GuestApp 'WindowsCareKit.exe'
$show = Join-Path $PSScriptRoot 'Show-InGuestApp.ps1'
$results = @()
foreach ($shot in $shots) {
    $out = Join-Path $OutDir $shot.File
    $appArgs = @('--lang','en')
    if ($shot.Screen) { $appArgs += @('--screen', $shot.Screen) }
    $label = if ($shot.Screen) { "--screen $($shot.Screen)" } else { 'bare' }
    Step "Capturing [$label] -> $($shot.File) ..."
    try {
        & $show -Exe $exeGuest -AppArgs $appArgs `
            -OutPng $out -SettleMs $SettleMs -VMName $VMName -GuestUser $GuestUser `
            -GuestPassword $sec -DisableProvenanceOverlay | Out-Null
        $ok = Test-Path $out
        $results += [pscustomobject]@{ Shot=$label; File=$shot.File; Ok=$ok }
        Info ($ok ? "saved $out" : "MISSING $out")
    } catch {
        $results += [pscustomobject]@{ Shot=$label; File=$shot.File; Ok=$false }
        Write-Host "    FAILED [$label]: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Step "Capture summary"
$results | Format-Table -AutoSize
$fail = @($results | Where-Object { -not $_.Ok }).Count
if ($fail -gt 0) { Write-Host "$fail shot(s) failed — see above." -ForegroundColor Yellow; exit 1 }
Write-Host "All $($results.Count) module-absent shots captured to $OutDir" -ForegroundColor Green
Write-Host "Visual-verify: 01 shows 6 nav items with NO Migration tab; 02 opens the default tab (deep-link ignored, no crash); 03 renders Backup normally." -ForegroundColor Green
