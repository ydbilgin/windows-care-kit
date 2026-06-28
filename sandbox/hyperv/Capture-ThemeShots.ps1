#requires -Version 7.0
<#
.SYNOPSIS
  Capture each WCK module in BOTH Dark and Light themes from the WCK-E2E guest, for a visual
  review of the new Light theme. Headless (no host window) — runs only in the disposable VM.
.DESCRIPTION
  Mirrors Capture-ReadmeShots.ps1 but loops themes via the app's `--theme dark|light` CLI flag.
  Output: <OutDir>\<theme>-<NN-module>.png  (e.g. light-04-migration.png, dark-04-migration.png).
.EXAMPLE
  pwsh -File Capture-ThemeShots.ps1
#>
[CmdletBinding()]
param(
    [string]   $VMName     = 'WCK-E2E',
    [string]   $GuestUser  = 'wck',
    [string]   $GuestPass  = 'WckE2E!2026',   # documented throwaway for the isolated guest (no value outside the VM)
    [string]   $OutDir     = 'F:\WCK-VM\shots\themes',
    [string]   $GuestApp   = 'C:\WCK-App',
    [int]      $SettleMs   = 2600,             # generous: async LoadAsync / Migration scan populate
    [string[]] $Themes     = @('dark','light'),
    [switch]   $SkipPublish
)
$ErrorActionPreference = 'Stop'
function Step($m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "    $m" -ForegroundColor DarkGray }

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$csproj     = Join-Path $repoRoot 'src\Suite.App.Wpf\Suite.App.Wpf.csproj'
$publishDir = 'F:\WCK-VM\wck-app\publish'
$dotnet     = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# Module key -> NN-ordered filename stem.
$modules = [ordered]@{
    'uninstall' = '01-uninstall'
    'clean'     = '02-clean'
    'backup'    = '03-backup'
    'migration' = '04-migration'
    'install'   = '05-reinstall'
    'settings'  = '06-settings'
}

if (-not $SkipPublish) {
    Step "Publishing WPF app (self-contained single-file win-x64, with the theme toggle)..."
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    & $dotnet publish $csproj -c Release --runtime win-x64 --self-contained true `
        -p:PublishSingleFile=true --output $publishDir 2>&1 | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
    Remove-Item (Join-Path $publishDir '*.pdb') -Force -ErrorAction SilentlyContinue
}
$exeHost = Join-Path $publishDir 'WindowsCareKit.exe'
if (-not (Test-Path $exeHost)) { throw "published exe not found: $exeHost (run without -SkipPublish)" }

Step "Ensuring capture agent in guest (starts VM if needed)..."
& (Join-Path $PSScriptRoot 'Install-CaptureAgent.ps1') -VMName $VMName -GuestUser $GuestUser -GuestPass $GuestPass

$sec  = ConvertTo-SecureString $GuestPass -AsPlainText -Force
$cred = [System.Management.Automation.PSCredential]::new(".\$GuestUser", $sec)

Step "Deploying fresh app build to guest $GuestApp ..."
$session = New-PSSession -VMName $VMName -Credential $cred
try {
    Invoke-Command -Session $session -ArgumentList $GuestApp -ScriptBlock {
        param($dst)
        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
    }
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $GuestApp -ToSession $session -Recurse -Force
    $deployed = Invoke-Command -Session $session -ArgumentList $GuestApp -ScriptBlock {
        param($d) Test-Path (Join-Path $d 'WindowsCareKit.exe')
    }
    if (-not $deployed) { throw "deploy verification failed: WindowsCareKit.exe missing in guest" }
} finally { Remove-PSSession $session -EA SilentlyContinue }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exeGuest = Join-Path $GuestApp 'WindowsCareKit.exe'
$show = Join-Path $PSScriptRoot 'Show-InGuestApp.ps1'
$results = @()
foreach ($theme in $Themes) {
    foreach ($mod in $modules.Keys) {
        $file = "$theme-$($modules[$mod]).png"
        $out  = Join-Path $OutDir $file
        Step "Capturing [$theme] '$mod' -> $file ..."
        try {
            & $show -Exe $exeGuest -AppArgs @('--lang','en','--theme',$theme,'--screen',$mod) `
                -OutPng $out -SettleMs $SettleMs -VMName $VMName -GuestUser $GuestUser `
                -GuestPassword $sec -DisableProvenanceOverlay | Out-Null
            $ok = Test-Path $out
            $results += [pscustomobject]@{ Theme=$theme; Module=$mod; File=$file; Ok=$ok }
            Info ($ok ? "saved $out" : "MISSING $out")
        } catch {
            $results += [pscustomobject]@{ Theme=$theme; Module=$mod; File=$file; Ok=$false }
            Write-Host "    FAILED [$theme] '$mod': $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Step "Capture summary"
$results | Format-Table -AutoSize
$fail = @($results | Where-Object { -not $_.Ok }).Count
if ($fail -gt 0) { Write-Host "$fail shot(s) failed — see above." -ForegroundColor Yellow; exit 1 }
Write-Host "All $($results.Count) theme shots captured to $OutDir" -ForegroundColor Green
