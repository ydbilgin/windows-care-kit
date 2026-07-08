#requires -Version 7.0
<#
.SYNOPSIS
  Capture clean, watermark-free, English per-module screenshots of the WCK WPF app from the
  WCK-E2E guest, for the README / docs. Headless (no host window).
.DESCRIPTION
  1. Publishes the WPF app (self-contained folder win-x64) on the host.
  2. Starts the guest + ensures the resident capture agent is running (Install-CaptureAgent.ps1).
  3. Deploys the fresh publish into the guest at C:\WCK-App over PowerShell Direct.
  4. For each module, launches the app with `--lang en --screen <module>` and captures JUST the
     window (provenance overlay disabled — these are presentation shots, not evidence shots).
  5. Pulls each PNG back to -OutDir on the host.

  The guest is a disposable, network-isolated eval VM; the autologon password is the documented
  throwaway from Install-CaptureAgent.ps1 (NOT a real secret — no value outside the VM).
.EXAMPLE
  pwsh -File Capture-ReadmeShots.ps1
#>
[CmdletBinding()]
param(
    [string]   $VMName     = 'WCK-E2E',
    [string]   $GuestUser  = 'wck',
    # Throwaway autologon credential for the disposable, network-isolated guest — same documented
    # value as Install-CaptureAgent.ps1; reverted on every checkpoint, no value outside the VM.
    [string]   $GuestPass  = 'WckE2E!2026',
    [string]   $OutDir     = 'F:\WCK-VM\shots\readme',
    [string]   $GuestApp   = 'C:\WCK-App',
    [int]      $SettleMs   = 2200,   # generous: Uninstall LoadAsync + Migration scan populate async
    [switch]   $SkipPublish
)
$ErrorActionPreference = 'Stop'
function Step($m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "    $m" -ForegroundColor DarkGray }

$repoRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$csproj    = Join-Path $repoRoot 'src\Suite.App.Wpf\Suite.App.Wpf.csproj'
$publishDir= 'F:\WCK-VM\wck-app\publish'
$dotnet    = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# Module key -> output filename (NN ordered, English). Settings last (it carries the language selector).
$modules = [ordered]@{
    'uninstall' = '01-uninstall.png'
    'clean'     = '02-clean.png'
    'backup'    = '03-backup.png'
    'migration' = '04-migration.png'
    'install'   = '05-reinstall.png'
    'settings'  = '06-settings.png'
}

if (-not $SkipPublish) {
    Step "Publishing WPF app (self-contained folder win-x64)..."
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    & $dotnet publish $csproj -c Release --runtime win-x64 --self-contained true `
        --output $publishDir 2>&1 | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
    Remove-Item (Join-Path $publishDir '*.pdb') -Force -ErrorAction SilentlyContinue
}
$exeHost = Join-Path $publishDir 'WindowsCareKit.exe'
if (-not (Test-Path $exeHost)) { throw "published exe not found: $exeHost (run without -SkipPublish)" }

# Ensure the guest is up and the capture agent is resident + fresh.
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

# Capture each module via the resident agent. Reuse Show-InGuestApp; provenance overlay OFF.
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exeGuest = Join-Path $GuestApp 'WindowsCareKit.exe'
$show = Join-Path $PSScriptRoot 'Show-InGuestApp.ps1'
$results = @()
foreach ($mod in $modules.Keys) {
    $out = Join-Path $OutDir $modules[$mod]
    Step "Capturing '$mod' -> $($modules[$mod]) ..."
    try {
        & $show -Exe $exeGuest -AppArgs @('--lang','en','--screen',$mod) `
            -OutPng $out -SettleMs $SettleMs -VMName $VMName -GuestUser $GuestUser `
            -GuestPassword $sec -DisableProvenanceOverlay | Out-Null
        $ok = Test-Path $out
        $results += [pscustomobject]@{ Module=$mod; File=$modules[$mod]; Ok=$ok }
        Info ($ok ? "saved $out" : "MISSING $out")
    } catch {
        $results += [pscustomobject]@{ Module=$mod; File=$modules[$mod]; Ok=$false }
        Write-Host "    FAILED '$mod': $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Step "Capture summary"
$results | Format-Table -AutoSize
$fail = @($results | Where-Object { -not $_.Ok }).Count
if ($fail -gt 0) { Write-Host "$fail module(s) failed — see above." -ForegroundColor Yellow; exit 1 }
Write-Host "All $($results.Count) English module shots captured to $OutDir" -ForegroundColor Green
