#requires -Version 5.1
<#
.SYNOPSIS
    Applies a synthetic WCK campaign persona manifest inside a disposable guest.

.DESCRIPTION
    Idempotently installs declared offline apps when their installers exist and writes
    synthetic seed files. No real owner data is read. A seed-manifest is emitted for
    verifier pre-state checks: each seed includes path, SHA-256, expected probe, and
    optional canary token marker.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('A','B')]
    [string] $Persona,

    [Parameter(Mandatory)]
    [string] $Manifest,

    [string] $StateRoot = 'C:\WCK-Persona',
    [string] $SeedManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Expand-WckPersonaPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    return [System.IO.Path]::GetFullPath($expanded)
}

function Get-WckFileSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
}

function Invoke-WckOfflineInstaller {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $App,
        [Parameter(Mandatory)] [string] $StateRoot
    )

    $appState = Join-Path $StateRoot ("apps\{0}.installed.json" -f $App.id)
    if (Test-Path -LiteralPath $appState) {
        return [pscustomobject]@{ id = $App.id; action = 'noop'; reason = 'state-present'; state = $appState }
    }

    if ([string]$App.installer -eq 'synthetic-seed' -or [string]$App.kind -eq 'synthetic') {
        New-Item -ItemType Directory -Force -Path (Split-Path $appState -Parent) | Out-Null
        @{ id = $App.id; kind = $App.kind; installedUtc = (Get-Date).ToUniversalTime().ToString('o'); action = 'synthetic-seed' } |
            ConvertTo-Json | Set-Content -LiteralPath $appState -Encoding UTF8
        return [pscustomobject]@{ id = $App.id; action = 'synthetic-seed'; state = $appState }
    }

    $installer = Expand-WckPersonaPath ([string]$App.installer)
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw "offline installer for '$($App.id)' not found: $installer"
    }

    $kind = [string]$App.kind
    switch ($kind) {
        'msi' {
            $args = @('/i', $installer, '/qn', '/norestart')
            $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList $args -Wait -PassThru
        }
        'inno' {
            $p = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
        }
        'nsis' {
            $p = Start-Process -FilePath $installer -ArgumentList @('/S') -Wait -PassThru
        }
        'peruser' {
            $p = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/MERGETASKS=!runcode') -Wait -PassThru
        }
        default {
            throw "unsupported installer kind for '$($App.id)': $kind"
        }
    }

    New-Item -ItemType Directory -Force -Path (Split-Path $appState -Parent) | Out-Null
    @{
        id = $App.id
        kind = $kind
        installer = $installer
        exitCode = $p.ExitCode
        installedUtc = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $appState -Encoding UTF8

    if ($p.ExitCode -ne 0) { throw "installer '$($App.id)' exited $($p.ExitCode)" }
    return [pscustomobject]@{ id = $App.id; action = 'installed'; exitCode = $p.ExitCode; state = $appState }
}

if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
    throw "manifest not found: $Manifest"
}

$manifestObject = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json -ErrorAction Stop
if ([string]$manifestObject.persona -ne $Persona) {
    throw "manifest persona '$($manifestObject.persona)' does not match -Persona '$Persona'"
}
if ($null -eq $manifestObject.expectedAdapterCount -or [int]$manifestObject.expectedAdapterCount -ne 0) {
    throw "expectedAdapterCount must be 0 for campaign manifests"
}

New-Item -ItemType Directory -Force -Path $StateRoot,(Join-Path $StateRoot 'apps'),(Join-Path $StateRoot 'seeds') | Out-Null
if ([string]::IsNullOrWhiteSpace($SeedManifestPath)) {
    $SeedManifestPath = Join-Path $StateRoot ("seed-manifest-persona-{0}.json" -f $Persona.ToLowerInvariant())
}

$installResults = @()
foreach ($app in @($manifestObject.apps)) {
    $installResults += Invoke-WckOfflineInstaller -App $app -StateRoot $StateRoot
}

$seedResults = @()
foreach ($seed in @($manifestObject.seeds)) {
    $target = Expand-WckPersonaPath ([string]$seed.path)
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null

    $content = [string]$seed.syntheticContent
    $hasCanary = $seed.PSObject.Properties.Match('canaryToken').Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$seed.canaryToken)
    if ($hasCanary) {
        $content = $content.TrimEnd() + "`n# WCK-CANARY: $($seed.canaryToken)`n"
    }

    $oldHash = if (Test-Path -LiteralPath $target -PathType Leaf) { Get-WckFileSha256 -Path $target } else { $null }
    $tmp = Join-Path (Split-Path $target -Parent) ('.wck-seed-' + [guid]::NewGuid().ToString('N') + '.tmp')
    Set-Content -LiteralPath $tmp -Value $content -Encoding UTF8
    $newHash = Get-WckFileSha256 -Path $tmp
    if ($oldHash -eq $newHash) {
        Remove-Item -LiteralPath $tmp -Force
        $action = 'noop'
    } else {
        Move-Item -LiteralPath $tmp -Destination $target -Force
        $action = 'written'
    }

    $seedResults += [pscustomobject]@{
        type = [string]$seed.type
        path = $target
        sha256 = $newHash
        action = $action
        expectedProbe = "file-exists+sha256:$newHash"
        hasCanaryToken = [bool]$hasCanary
    }
}

$honesty = @($manifestObject.honestyCells | ForEach-Object {
    [pscustomobject]@{ name = [string]$_.name; expectedWarning = [string]$_.expectedWarning }
})

$out = [ordered]@{
    persona = $Persona
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    expectedAdapterCount = [int]$manifestObject.expectedAdapterCount
    apps = $installResults
    seeds = $seedResults
    honestyCells = $honesty
}
$out | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $SeedManifestPath -Encoding UTF8
$out
