#requires -Version 5.1
<#
.SYNOPSIS
    Persona-B uninstall guest runner: no Persona-A relabeling.

.DESCRIPTION
    Runs inside the disposable guest after Initialize-WckPersona has already installed
    qBittorrent + Chrome and written synthetic Steam/Discord/Spotify seeds. It executes
    no uninstallers: qBittorrent is a manual registry witness and Chrome is an
    unattended-decline witness because its registry command has no silent switch. Synthetic
    apps are reported as synthetic-seed and never claimed as real installs.
#>
[CmdletBinding()]
param(
    [string] $InputDir = 'C:\WCK-Input',
    [string] $Output   = 'C:\WCK-Output',
    [string] $PersonaStateRoot = 'C:\WCK-Persona'
)

$ErrorActionPreference = 'Continue'
$harness = Join-Path $InputDir 'harness\UninstallE2E.exe'
New-Item -ItemType Directory -Force -Path $Output | Out-Null

function Log([string]$m){
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $Output 'guest-progress.log') -Value $line
}

$env:WCK_DISPOSABLE_MACHINE = '1'
'disposable' | Set-Content -LiteralPath (Join-Path $env:TEMP 'wck-disposable.marker')
Log "disposable-machine opt-in set (env + marker)."

Log "closing qBittorrent/Chrome before unattended uninstall..."
Get-Process qbittorrent,chrome -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$seedManifest = Join-Path $PersonaStateRoot 'seed-manifest-persona-b.json'
if (Test-Path -LiteralPath $seedManifest) {
    Copy-Item -LiteralPath $seedManifest -Destination (Join-Path $Output 'persona-seed-manifest.json') -Force
}

$seed = $null
try {
    if (Test-Path -LiteralPath $seedManifest) {
        $seed = Get-Content -LiteralPath $seedManifest -Raw | ConvertFrom-Json -ErrorAction Stop
    }
} catch {
    Log "WARNING: could not parse persona seed manifest: $($_.Exception.Message)"
}

$appDispositions = @()
foreach ($id in 'qbittorrent','chrome-enterprise','steam','discord','spotify') {
    $seedApp = if ($seed) { @($seed.apps | Where-Object { $_.id -eq $id } | Select-Object -First 1) } else { @() }
    $action = if ($seedApp.Count) { [string]$seedApp[0].action } else { 'unknown' }
    $source = if ($id -in @('steam','discord','spotify')) { 'synthetic-seed' } else { 'real-installed' }
    $scope = switch ($id) {
        'qbittorrent' { 'required-not-executed-manual-witness' }
        'chrome-enterprise' { 'required-not-executed-unattended-decline' }
        default { 'uninstall-out-of-scope' }
    }
    $appDispositions += [ordered]@{
        appId = $id
        source = $source
        seedAction = $action
        uninstallScope = $scope
    }
}

[ordered]@{
    persona = 'B'
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    apps = $appDispositions
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Output 'persona-b-disposition.json') -Encoding UTF8

if (-not (Test-Path $harness)) {
    Log "FATAL: harness not found at $harness"
    return [pscustomobject]@{ ExitCode=99; Result='NO-HARNESS'; Persona='B' }
}

Log "running UninstallE2E.exe (Persona-B execute none; require qbittorrent,chrome-enterprise)..."
$consoleLog = Join-Path $Output 'harness-console.log'
$proc = Start-Process -FilePath $harness `
    -ArgumentList "--output `"$Output`" --require qbittorrent,chrome-enterprise --settleSeconds 90 --execTimeoutSeconds 180" `
    -Wait -PassThru -RedirectStandardOutput $consoleLog -RedirectStandardError (Join-Path $Output 'harness-stderr.log')
$rc = $proc.ExitCode
Log "harness exited rc=$rc"

$result = switch ($rc) { 0 {'PASS'} 3 {'GUARD-REFUSED'} default {"FAIL($rc)"} }
$rc | Set-Content -LiteralPath (Join-Path $Output 'harness-exitcode.txt')
$result | Set-Content -LiteralPath (Join-Path $Output 'uninstall-e2e-result.txt')
Log "RESULT: $result"

[pscustomobject]@{ ExitCode = $rc; Result = $result; Persona = 'B'; Dispositions = $appDispositions }
