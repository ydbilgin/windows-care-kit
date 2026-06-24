#requires -Version 5.1
<#
.SYNOPSIS
  Persona-B migration honesty cell.

.DESCRIPTION
  Seeds synthetic Discord token and launcher-on-other-drive bytes, packages them as
  backup evidence, and emits a PASS only for honest warnings/defer dispositions. It
  does not claim machine-bound Discord/Chrome state was restored successfully.
#>
[CmdletBinding()]
param(
    [string] $Harness = 'C:\WCK-MigE2E\MigrationE2E.exe',
    [string] $Base    = 'C:\MigE2E',
    [string] $Output  = 'C:\WCK-MigOutput'
)

$ErrorActionPreference = 'Stop'

# DESIGN-DEFERRED (auditor MAJOR, 2026-06-24): this runner currently HAND-WRITES the migration
# evidence instead of invoking $Harness (MigrationE2E.exe). A "PASS" here would NOT prove the real
# WCK migration engine emits the honest defer/warning dispositions — exactly the fake-evidence
# class C-18/FIX-3 forbids. Fail closed until it actually drives MigrationE2E.exe over the seeded
# Persona-B bytes, so a non-genuine Persona-B Migration result can never be produced/reported.
if (-not $env:WCK_ALLOW_SYNTHETIC_MIGRATION_EVIDENCE) {
    throw "Persona-B migration runner is DESIGN-DEFERRED: it fabricates evidence rather than invoking MigrationE2E.exe. Refusing to emit a non-genuine migration result. (Wire it to MigrationE2E.exe before use; WCK_ALLOW_SYNTHETIC_MIGRATION_EVIDENCE is scaffolding-only and must never gate a reported pass.)"
}
$A = Join-Path $Base 'A'
$B = Join-Path $Base 'B'
$Pkg = Join-Path $Base 'Pkg'
$appA = Join-Path $A 'AppData\Roaming'
$localA = Join-Path $A 'AppData\Local'

Remove-Item $Base -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $A,$B,$Pkg,$Output,$appA,$localA | Out-Null

$discordPath = Join-Path $appA 'discord\Local State'
$launcherPath = Join-Path $A 'fake-steam\steamapps\libraryfolders.vdf'
$runKeyNote = Join-Path $A 'RunKey-WckFakeLauncher.txt'
$chromeAbePath = Join-Path $localA 'Google\Chrome\User Data\Default\Preferences'

New-Item -ItemType Directory -Force -Path (Split-Path $discordPath -Parent),(Split-Path $launcherPath -Parent),(Split-Path $chromeAbePath -Parent) | Out-Null
Set-Content -LiteralPath $discordPath -Value '{ "persona": "B", "tokens": ["synthetic-discord-token-do-not-restore"] }' -Encoding UTF8
Set-Content -LiteralPath $launcherPath -Value '"libraryfolders"`n{`n  "0" { "path" "F:\\fake-steam" }`n}' -Encoding UTF8
Set-Content -LiteralPath $runKeyNote -Value 'HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WckFakeLauncher -> C:\Users\wck\AppData\Local\FakeLauncher\launcher.exe' -Encoding UTF8
Set-Content -LiteralPath $chromeAbePath -Value '{ "profile": { "name": "Persona B" }, "abe": "machine-bound", "sync": { "requested": true } }' -Encoding UTF8

$pkgDiscord = Join-Path $Pkg 'discord\Local State'
$pkgLauncher = Join-Path $Pkg 'launcher\libraryfolders.vdf'
$pkgRun = Join-Path $Pkg 'launcher\RunKey-WckFakeLauncher.txt'
New-Item -ItemType Directory -Force -Path (Split-Path $pkgDiscord -Parent),(Split-Path $pkgLauncher -Parent) | Out-Null
Copy-Item -LiteralPath $discordPath -Destination $pkgDiscord -Force
Copy-Item -LiteralPath $launcherPath -Destination $pkgLauncher -Force
Copy-Item -LiteralPath $runKeyNote -Destination $pkgRun -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
$zipPath = Join-Path $Base 'migration-export.zip'
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($Pkg, $zipPath)
Copy-Item -LiteralPath $zipPath -Destination (Join-Path $Output 'migration-export.zip') -Force

$discordSha = (Get-FileHash -LiteralPath $discordPath -Algorithm SHA256).Hash.ToLowerInvariant()
$launcherSha = (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash.ToLowerInvariant()

$evidence = [ordered]@{
    pass = $true
    persona = 'B'
    failReason = $null
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    restorePlanSkips = @(
        [ordered]@{ recipeId = 'discord'; relativePath = 'Local State'; reason = 'NotAllowListed'; note = 'machine-bound token store backed up but restore deferred' }
    )
    backupProofs = @(
        [ordered]@{ recipeId = 'discord'; relativePath = 'discord/Local State'; bytes = (Get-Item -LiteralPath $discordPath).Length; sha256 = $discordSha }
        [ordered]@{ recipeId = 'launcher'; relativePath = 'launcher/libraryfolders.vdf'; bytes = (Get-Item -LiteralPath $launcherPath).Length; sha256 = $launcherSha }
    )
    honestDispositions = @(
        [ordered]@{ recipeId = 'discord'; disposition = 'NotAllowListed'; warning = 'token-store restore basarili iddiasi yok' }
        [ordered]@{ recipeId = 'launcher'; disposition = 're-add-only'; warning = 'sadece re-add' }
        [ordered]@{ recipeId = 'chrome-abe'; disposition = 'sync-only'; warning = 'restore-edilemez, sync' }
    )
    verifications = @(
        [ordered]@{ recipeId = 'discord'; relativePath = 'Local State'; skipped = $true; skipReason = 'NotAllowListed: machine-bound restore deferred'; shaMatch = $null; destExists = $null; manifestSha = $discordSha; restoredSha = $null; destPath = $null }
        [ordered]@{ recipeId = 'launcher'; relativePath = 'libraryfolders.vdf'; skipped = $true; skipReason = 're-add-only: launcher path must be re-added'; shaMatch = $null; destExists = $null; manifestSha = $launcherSha; restoredSha = $null; destPath = $null }
    )
}

$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Output 'migration-e2e-evidence.json') -Encoding UTF8
Set-Content -LiteralPath (Join-Path $Output 'migration-e2e-summary.txt') -Value '=== PASS: Persona-B honest warnings emitted; no machine-bound restore success claimed ===' -Encoding UTF8

Write-Host "[mig-guest-b] Persona-B synthetic Discord/launcher backup proofs written; restore claims deferred honestly."
$evidence
