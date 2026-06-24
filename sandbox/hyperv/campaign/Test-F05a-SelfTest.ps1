#requires -Version 7.0
<#
.SYNOPSIS
    HOST-SAFE self-test for the F0.5a campaign harness (decision C-20 proof) — DISCRIMINATING.

.DESCRIPTION
    Proves — WITHOUT any real VM, real uninstall, or any host mutation — that:
      * the C-16/FIX-A guard (Guard-WckDisposable.ps1) REFUSES a non-campaign VM, an
        out-of-root path, a `..` traversal, AND a REAL junction under the root whose target
        leaves the root (both existing-leaf and nonexistent-leaf), and ACCEPTS a marker'd VM
        + a genuinely-in-root path;
      * the C-1/FIX-B verifier (Test-WckCampaignEvidence.ps1) returns Pass=$true on a
        well-formed FRESH synthetic fixture, and Pass=$false on EACH deliberately-broken
        fixture — including the NEW non-vacuity cases (stale file, malformed hash JSON,
        after>=reset ordering, unreadable file) and proves an extra artifact MOVES the digest.

    FIX-H: every NEW case is one the OLD code would have FAILED (it would have wrongly
    ALLOWED the junction escape, or wrongly returned Pass=$true on the stale/malformed
    fixture). All fixtures are SYNTHETIC under a scratch temp dir this script creates+removes.
    The secret-pattern fixture uses an OBVIOUSLY-FAKE placeholder ("ghp_FAKE0000...") that
    matches the regex SHAPE but is not a real token.

    Exit 0 if every assertion matches its expectation; exit 1 otherwise.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
. "$here\Guard-WckDisposable.ps1"
. "$here\Test-WckCampaignEvidence.ps1"
. "$here\Invoke-WckCampaignCell.ps1"
. "$here\New-WckCampaignClone.ps1"
. "$here\WckScreenshotProvenance.ps1"

# Scratch root (never the campaign root, never the host C:\ tree we care about).
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("wck-f05a-selftest-{0}" -f ([guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
function Record([string]$name, [bool]$ok, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Case = $name; Pass = $ok; Detail = $detail })
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}{2}" -f $tag, $name, ($(if ($detail) { "  ($detail)" } else { '' }))) -ForegroundColor $color
}

# Synthetic GUID for the marker (NOT a secret).
$fakeGuid = '11111111-2222-3333-4444-555555555555'

# --- helper: build a WELL-FORMED FRESH synthetic evidence dir -----------------------------
# FIX-B-aware: dir-hashes carry git+vscode files (BEFORE) / none (AFTER); manifest stamps
# runStartUtc and afterSnapshotUtc < resetUtc; all files written during the run window.
# result.txt=PASS.
function New-GoodEvidence {
    param([string] $Dir)
    $runStartUtc = (Get-Date).ToUniversalTime().AddSeconds(-5)
    $before = Join-Path $Dir 'before'; $after = Join-Path $Dir 'after'
    New-Item -ItemType Directory -Force -Path $Dir,$before,$after | Out-Null

    $regBefore = @'
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Git_is1]
"DisplayName"="Git"
"InstallLocation"="C:\\Program Files\\Git\\"

[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1]
"DisplayName"="Microsoft Visual Studio Code"
"InstallLocation"="C:\\Program Files\\Microsoft VS Code\\"
'@
    $regAfter = @'
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{23170F69-40C1-2702-2409-000001000000}]
"DisplayName"="7-Zip 24.09 (x64)"

[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Notepad++]
"DisplayName"="Notepad++ (64-bit x64)"
'@
    Set-Content -LiteralPath (Join-Path $before 'uninstall-registry.reg') -Value $regBefore -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $after  'uninstall-registry.reg') -Value $regAfter  -Encoding UTF8

    # FIX-B: BEFORE dir-hashes carry git + vscode files; AFTER is empty -> real delta.
    $hashBefore = @(
        [pscustomobject]@{ Path = 'C:\Program Files\Git\git.exe';                   Sha256 = 'A1B2C3D4E5F600000000000000000000000000000000000000000000000000AA' }
        [pscustomobject]@{ Path = 'C:\Program Files\Microsoft VS Code\Code.exe';     Sha256 = 'B1B2C3D4E5F600000000000000000000000000000000000000000000000000BB' }
    ) | ConvertTo-Json -Depth 4
    $hashAfter = '[]'
    Set-Content -LiteralPath (Join-Path $before 'dir-hashes.json') -Value $hashBefore -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $after  'dir-hashes.json') -Value $hashAfter  -Encoding UTF8

    $ue = [ordered]@{
        generated = (Get-Date).ToUniversalTime().ToString('o')
        pass      = $true
        verdict   = 'PASS'
        branchMismatch = @()
        focus = @(
            [ordered]@{ targetId = '7zip';      found = $true; classification = 'BLOCK' }
            [ordered]@{ targetId = 'git';       found = $true; classification = 'ALLOW' }
            [ordered]@{ targetId = 'vscode';    found = $true; classification = 'ALLOW' }
            [ordered]@{ targetId = 'notepadpp'; found = $true; classification = 'MANUAL' }
        )
        executions = @(
            [ordered]@{ targetId = 'git';    skipped = $false; removedFromRegistry = $true }
            [ordered]@{ targetId = 'vscode'; skipped = $false; removedFromRegistry = $true }
        )
    } | ConvertTo-Json -Depth 6
    Set-Content -LiteralPath (Join-Path $Dir 'uninstall-e2e-evidence.json') -Value $ue -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Dir 'uninstall-e2e-result.txt') -Value 'PASS' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Dir 'harness-exitcode.txt') -Value '0' -Encoding UTF8

    @{ state = 'Off'; checkpoint = 'baseline-clean' } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $Dir 'vm-final-state.json') -Encoding UTF8

    # FIX-C: stamp sub-second afterSnapshotUtc < resetUtc; generatedUtc is an end-of-run stamp.
    $afterUtc = $runStartUtc.AddSeconds(2).ToString('o')
    $resetUtc = $runStartUtc.AddSeconds(2.3).ToString('o')
    $generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    @{
        runId        = 'run-20260624-aaaa1111'
        vmName       = 'WCK-E2E'
        persona      = 'A'
        module       = 'Uninstall'
        checkpoint   = 'baseline-clean'
        campaignGuid = $fakeGuid
        runStartUtc     = $runStartUtc.ToString('o')
        generatedUtc     = $generatedUtc
        afterSnapshotUtc = $afterUtc
        resetUtc         = $resetUtc
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Dir 'cell-manifest.json') -Encoding UTF8
}

function New-GoodPersonaBUninstallEvidence {
    param([string] $Dir)
    $runStartUtc = (Get-Date).ToUniversalTime().AddSeconds(-5)
    $before = Join-Path $Dir 'before'; $after = Join-Path $Dir 'after'
    New-Item -ItemType Directory -Force -Path $Dir,$before,$after | Out-Null

    $regBefore = @'
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\qBittorrent]
"DisplayName"="qBittorrent"
"InstallLocation"="C:\\Program Files\\qBittorrent\\"

[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{CHROME-PERSONA-B}]
"DisplayName"="Google Chrome"
'@
    $regAfter = @'
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\qBittorrent]
"DisplayName"="qBittorrent"
"InstallLocation"=""

[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{CHROME-PERSONA-B}]
"DisplayName"="Google Chrome"
'@
    Set-Content -LiteralPath (Join-Path $before 'uninstall-registry.reg') -Value $regBefore -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $after  'uninstall-registry.reg') -Value $regAfter  -Encoding UTF8

    @(
        [pscustomobject]@{ Path = 'C:\Program Files\qBittorrent\qbittorrent.exe'; Sha256 = 'C1B2C3D4E5F600000000000000000000000000000000000000000000000000CC' }
        [pscustomobject]@{ Path = 'C:\WCK-Persona\seed-manifest-persona-b.json'; Sha256 = 'D1B2C3D4E5F600000000000000000000000000000000000000000000000000DD' }
    ) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $before 'dir-hashes.json') -Encoding UTF8
    @(
        [pscustomobject]@{ Path = 'C:\WCK-Persona\seed-manifest-persona-b.json'; Sha256 = 'D1B2C3D4E5F600000000000000000000000000000000000000000000000000DD' }
    ) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $after 'dir-hashes.json') -Encoding UTF8

    [ordered]@{
        generated = (Get-Date).ToUniversalTime().ToString('o')
        pass = $true
        verdict = 'PASS'
        executeSet = @()
        unprovenExecutions = @()
        branchMismatch = @()
        focus = @(
            [ordered]@{ targetId = 'qbittorrent'; found = $true; classification = 'MANUAL' }
            [ordered]@{ targetId = 'chrome-enterprise'; found = $true; classification = 'ALLOW'; silentCapable = $false }
        )
        executions = @()
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Dir 'uninstall-e2e-evidence.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Dir 'uninstall-e2e-result.txt') -Value 'PASS' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Dir 'harness-exitcode.txt') -Value '0' -Encoding UTF8

    [ordered]@{
        persona = 'B'
        apps = @(
            [ordered]@{ id = 'qbittorrent'; action = 'installed' }
            [ordered]@{ id = 'chrome-enterprise'; action = 'installed' }
            [ordered]@{ id = 'steam'; action = 'synthetic-seed' }
            [ordered]@{ id = 'discord'; action = 'synthetic-seed' }
            [ordered]@{ id = 'spotify'; action = 'synthetic-seed' }
        )
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Dir 'persona-seed-manifest.json') -Encoding UTF8
    [ordered]@{
        persona = 'B'
        generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        apps = @(
            [ordered]@{ appId = 'qbittorrent'; source = 'real-installed'; seedAction = 'installed'; uninstallScope = 'required-not-executed-manual-witness' }
            [ordered]@{ appId = 'chrome-enterprise'; source = 'real-installed'; seedAction = 'installed'; uninstallScope = 'required-not-executed-unattended-decline' }
            [ordered]@{ appId = 'steam'; source = 'synthetic-seed'; seedAction = 'synthetic-seed'; uninstallScope = 'uninstall-out-of-scope' }
            [ordered]@{ appId = 'discord'; source = 'synthetic-seed'; seedAction = 'synthetic-seed'; uninstallScope = 'uninstall-out-of-scope' }
            [ordered]@{ appId = 'spotify'; source = 'synthetic-seed'; seedAction = 'synthetic-seed'; uninstallScope = 'uninstall-out-of-scope' }
        )
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Dir 'persona-b-disposition.json') -Encoding UTF8

    @{ state = 'Off'; checkpoint = 'baseline-clean' } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $Dir 'vm-final-state.json') -Encoding UTF8

    $afterUtc = $runStartUtc.AddSeconds(2).ToString('o')
    $resetUtc = $runStartUtc.AddSeconds(2.3).ToString('o')
    @{
        runId        = 'run-20260624-bbbb2222'
        vmName       = 'WCK-E2E'
        persona      = 'B'
        module       = 'Uninstall'
        checkpoint   = 'baseline-clean'
        campaignGuid = $fakeGuid
        runStartUtc     = $runStartUtc.ToString('o')
        generatedUtc     = (Get-Date).ToUniversalTime().ToString('o')
        afterSnapshotUtc = $afterUtc
        resetUtc         = $resetUtc
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Dir 'cell-manifest.json') -Encoding UTF8
}

function New-CommonCellManifest {
    param([string] $Dir, [string] $Module)
    $runStartUtc = (Get-Date).ToUniversalTime().AddSeconds(-5)
    $afterUtc = $runStartUtc.AddSeconds(2).ToString('o')
    $resetUtc = $runStartUtc.AddSeconds(2.3).ToString('o')
    @{
        runId        = "run-20260624-$($Module.ToLowerInvariant())"
        vmName       = 'WCK-E2E'
        persona      = 'A'
        module       = $Module
        checkpoint   = 'baseline-clean'
        campaignGuid = $fakeGuid
        runStartUtc     = $runStartUtc.ToString('o')
        generatedUtc     = (Get-Date).ToUniversalTime().ToString('o')
        afterSnapshotUtc = $afterUtc
        resetUtc         = $resetUtc
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Dir 'cell-manifest.json') -Encoding UTF8
}

function New-GoodMigrationEvidence {
    param([string] $Dir)
    $before = Join-Path $Dir 'before'; $after = Join-Path $Dir 'after'
    $pkg = Join-Path $Dir 'pkg'
    New-Item -ItemType Directory -Force -Path $Dir,$before,$after,$pkg | Out-Null

    $gitDest = 'C:\MigE2E\B\.gitconfig'
    $claudeDest = 'C:\MigE2E\B\.claude\CLAUDE.md'
    $gitSha = '1111111111111111111111111111111111111111111111111111111111111111'
    $claudeSha = '2222222222222222222222222222222222222222222222222222222222222222'

    Set-Content -LiteralPath (Join-Path $before 'dir-hashes.json') -Value '[]' -Encoding UTF8
    @(
        [pscustomobject]@{ Path = $gitDest; Sha256 = $gitSha }
        [pscustomobject]@{ Path = $claudeDest; Sha256 = $claudeSha }
    ) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $after 'dir-hashes.json') -Encoding UTF8

    $skillDir = Join-Path $pkg '.claude\skills\demo'
    $noteDir = Join-Path $pkg '.claude\projects\demo\memory'
    New-Item -ItemType Directory -Force -Path $skillDir,$noteDir | Out-Null
    Set-Content -LiteralPath (Join-Path $skillDir 'SKILL.md') -Value '# Demo skill' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $noteDir 'note.md') -Value '# Demo note' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $pkg '.gitconfig') -Value '[user]' -Encoding ascii
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zipPath = Join-Path $Dir 'migration-export.zip'
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($pkg, $zipPath)
    Remove-Item -LiteralPath $pkg -Recurse -Force

    [ordered]@{
        pass = $true
        failReason = $null
        generatedAt = (Get-Date).ToUniversalTime().ToString('o')
        restorePlanSkips = @(
            [ordered]@{ recipeId = 'discord'; relativePath = 'settings.json'; reason = 'NotAllowListed'; note = 'machine-locked restore deferred' }
        )
        verifications = @(
            [ordered]@{ recipeId = 'git.config'; relativePath = '.gitconfig'; skipped = $false; skipReason = $null; shaMatch = $true; destExists = $true; manifestSha = $gitSha; restoredSha = $gitSha; destPath = $gitDest }
            [ordered]@{ recipeId = 'anthropic.claude-code'; relativePath = '.claude/CLAUDE.md'; skipped = $false; skipReason = $null; shaMatch = $true; destExists = $true; manifestSha = $claudeSha; restoredSha = $claudeSha; destPath = $claudeDest }
            [ordered]@{ recipeId = 'discord'; relativePath = 'settings.json'; skipped = $true; skipReason = 'NotAllowListed: machine-locked restore deferred'; shaMatch = $null; destExists = $null; manifestSha = '3333333333333333333333333333333333333333333333333333333333333333'; restoredSha = $null; destPath = $null }
        )
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Dir 'migration-e2e-evidence.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Dir 'migration-e2e-summary.txt') -Value '=== PASS ===' -Encoding UTF8
    @{ state = 'Off'; checkpoint = 'baseline-clean' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Dir 'vm-final-state.json') -Encoding UTF8
    New-CommonCellManifest -Dir $Dir -Module 'Migration'
}

function New-GoodPersonaBMigrationEvidence {
    param([string] $Dir)
    $before = Join-Path $Dir 'before'; $after = Join-Path $Dir 'after'
    $pkg = Join-Path $Dir 'pkg'
    New-Item -ItemType Directory -Force -Path $Dir,$before,$after,$pkg | Out-Null
    Set-Content -LiteralPath (Join-Path $before 'dir-hashes.json') -Value '[]' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $after 'dir-hashes.json') -Value '[]' -Encoding UTF8

    $discordDir = Join-Path $pkg 'discord'
    $launcherDir = Join-Path $pkg 'launcher'
    New-Item -ItemType Directory -Force -Path $discordDir,$launcherDir | Out-Null
    Set-Content -LiteralPath (Join-Path $discordDir 'Local State') -Value '{ "tokens": ["synthetic-discord-token-do-not-restore"] }' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $launcherDir 'libraryfolders.vdf') -Value '"libraryfolders" { "0" { "path" "F:\\fake-steam" } }' -Encoding ascii
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zipPath = Join-Path $Dir 'migration-export.zip'
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($pkg, $zipPath)
    Remove-Item -LiteralPath $pkg -Recurse -Force

    [ordered]@{
        pass = $true
        persona = 'B'
        failReason = $null
        generatedAt = (Get-Date).ToUniversalTime().ToString('o')
        restorePlanSkips = @(
            [ordered]@{ recipeId = 'discord'; relativePath = 'Local State'; reason = 'NotAllowListed'; note = 'machine-bound token store backed up but restore deferred' }
        )
        backupProofs = @(
            [ordered]@{ recipeId = 'discord'; relativePath = 'discord/Local State'; bytes = 54; sha256 = '3333333333333333333333333333333333333333333333333333333333333333' }
            [ordered]@{ recipeId = 'launcher'; relativePath = 'launcher/libraryfolders.vdf'; bytes = 51; sha256 = '4444444444444444444444444444444444444444444444444444444444444444' }
        )
        honestDispositions = @(
            [ordered]@{ recipeId = 'discord'; disposition = 'NotAllowListed'; warning = 'token-store restore basarili iddiasi yok' }
            [ordered]@{ recipeId = 'launcher'; disposition = 're-add-only'; warning = 'sadece re-add' }
            [ordered]@{ recipeId = 'chrome-abe'; disposition = 'sync-only'; warning = 'restore-edilemez, sync' }
        )
        verifications = @(
            [ordered]@{ recipeId = 'discord'; relativePath = 'Local State'; skipped = $true; skipReason = 'NotAllowListed: machine-bound restore deferred'; shaMatch = $null; destExists = $null; manifestSha = '3333333333333333333333333333333333333333333333333333333333333333'; restoredSha = $null; destPath = $null }
            [ordered]@{ recipeId = 'launcher'; relativePath = 'libraryfolders.vdf'; skipped = $true; skipReason = 're-add-only: launcher path must be re-added'; shaMatch = $null; destExists = $null; manifestSha = '4444444444444444444444444444444444444444444444444444444444444444'; restoredSha = $null; destPath = $null }
        )
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Dir 'migration-e2e-evidence.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Dir 'migration-e2e-summary.txt') -Value '=== PASS: Persona-B honest warnings emitted ===' -Encoding UTF8
    @{ state = 'Off'; checkpoint = 'baseline-clean' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Dir 'vm-final-state.json') -Encoding UTF8
    New-CommonCellManifest -Dir $Dir -Module 'Migration'
    $mPath = Join-Path $Dir 'cell-manifest.json'
    $m = Get-Content -LiteralPath $mPath -Raw | ConvertFrom-Json
    $m.persona = 'B'
    $m | ConvertTo-Json | Set-Content -LiteralPath $mPath -Encoding UTF8
}

function New-GoodCleanEvidence {
    param([string] $Dir)
    $before = Join-Path $Dir 'before'; $after = Join-Path $Dir 'after'
    New-Item -ItemType Directory -Force -Path $Dir,$before,$after | Out-Null
    $junkPath = 'C:\Users\wck\AppData\Local\Temp\WCK-CleanE2E-Witness\junk.txt'
    $junkSha = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
    @([pscustomobject]@{ Path = $junkPath; Sha256 = $junkSha }) |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $before 'dir-hashes.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $after 'dir-hashes.json') -Value '[]' -Encoding UTF8

    $beforeReg = @'
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run]
"WCK-CleanE2E-Witness"="C:\\FAKE-WCK-CLEANE2E.exe"
'@
    $afterReg = @'
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run]
'@
    Set-Content -LiteralPath (Join-Path $before 'run-registry.reg') -Value $beforeReg -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $after 'run-registry.reg') -Value $afterReg -Encoding UTF8

    $decisions = @(
        [ordered]@{ name = 'P1-junk-in-plan'; label = 'junk dir entered the plan'; expectedAllowed = $true; actualAllowed = $true; pass = $true }
        [ordered]@{ name = 'P2-protected-refused'; label = 'System32 path refused by gate'; expectedAllowed = $false; actualAllowed = $false; pass = $true }
        [ordered]@{ name = 'P3-value-delete-allowed'; label = 'Run value-delete gate allow'; expectedAllowed = $true; actualAllowed = $true; pass = $true }
        [ordered]@{ name = 'P3-key-delete-refused'; label = 'protected key-delete refused'; expectedAllowed = $false; actualAllowed = $false; pass = $true }
        [ordered]@{ name = 'P1-ground-truth'; label = 'junk dir gone after execute'; expectedAllowed = $true; actualAllowed = $true; pass = $true }
        [ordered]@{ name = 'P2-ground-truth'; label = 'protected path still exists after execute'; expectedAllowed = $true; actualAllowed = $true; pass = $true }
        [ordered]@{ name = 'P3-ground-truth'; label = 'Run value gone after execute'; expectedAllowed = $true; actualAllowed = $true; pass = $true }
    )
    [ordered]@{
        generated = (Get-Date).ToUniversalTime().ToString('o')
        pass = $true
        verdict = 'PASS'
        gateDecisions = $decisions
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Dir 'clean-e2e-evidence.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Dir 'clean-e2e-summary.txt') -Value '=== PASS ===' -Encoding UTF8
    @{ state = 'Off'; checkpoint = 'baseline-clean' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Dir 'vm-final-state.json') -Encoding UTF8
    New-CommonCellManifest -Dir $Dir -Module 'Clean'
}

Write-Host ''
Write-Host '=== F0.5a HOST-SAFE SELF-TEST (no VM, synthetic fixtures only) ===' -ForegroundColor Cyan
Write-Host ''

# =========================================================================================
# GUARD TESTS (D1) — including FIX-A reparse-point escape
# =========================================================================================
Write-Host '-- Guard (Guard-WckDisposable.ps1) --' -ForegroundColor Cyan

# (a) marker-less VM Notes -> Assert-WckDisposableVM THROWS
$threw = $false
try { Assert-WckDisposableVM -VMName 'NotACampaignVM' -VMObject ([pscustomobject]@{ Name = 'NotACampaignVM'; Notes = 'just an ordinary VM' }) }
catch { $threw = $true }
Record 'guard.a marker-less VM Notes -> THROW' $threw

# (a2) FIX-E: injected VM name mismatch -> THROW (fixture must describe the requested VM)
$threw = $false
try { Assert-WckDisposableVM -VMName 'WCK-E2E' -VMObject ([pscustomobject]@{ Name = 'SOME-OTHER-VM'; Notes = "WCK-CAMPAIGN:$fakeGuid" }) }
catch { $threw = $true }
Record 'guard.a2 injected name != -VMName -> THROW (FIX-E)' $threw

# Build a REAL campaign-root + a junction that ESCAPES it (FIX-A). The root and the escape
# target both live under the scratch tree, so nothing host-sensitive is touched.
$realRoot   = Join-Path $scratch 'campaign-root'          # the pinned root analogue
$outsideTgt = Join-Path $scratch 'OUTSIDE-the-root'       # a sibling, NOT under the root
New-Item -ItemType Directory -Force -Path $realRoot, $outsideTgt | Out-Null
Set-Content -LiteralPath (Join-Path $outsideTgt 'real-vm.vhdx') -Value 'pretend-vhdx' -Encoding UTF8

$junctionMade = $false
$escapeJunction = Join-Path $realRoot 'escape'           # lives UNDER root, POINTS outside
try {
    New-Item -ItemType Junction -Path $escapeJunction -Target $outsideTgt -ErrorAction Stop | Out-Null
    $junctionMade = Test-Path -LiteralPath $escapeJunction
} catch { $junctionMade = $false }

# (b) out-of-root path -> Assert-WckPathUnderRoot THROWS (root must really exist now)
$threw = $false
try { Assert-WckPathUnderRoot -Path 'C:\Windows\System32' -Root $realRoot }
catch { $threw = $true }
Record 'guard.b out-of-root path (System32) -> THROW' $threw

# (b2) traversal escape via .. -> THROWS
$threw = $false
try { Assert-WckPathUnderRoot -Path (Join-Path $realRoot '..\real-vm.vhdx') -Root $realRoot }
catch { $threw = $true }
Record 'guard.b2 .. traversal escape -> THROW' $threw

# (b3) FIX-A: junction UNDER root pointing OUTSIDE, EXISTING leaf -> THROW (old code ALLOWED)
if ($junctionMade) {
    $existingLeaf = Join-Path $escapeJunction 'real-vm.vhdx'   # resolves to outsideTgt\real-vm.vhdx
    $threw = $false
    try { Assert-WckPathUnderRoot -Path $existingLeaf -Root $realRoot }
    catch { $threw = $true }
    Record 'guard.b3 FIX-A junction escape (existing leaf) -> THROW' $threw
} else {
    Record 'guard.b3 FIX-A junction escape (existing leaf) -> THROW' $false 'could not create test junction'
}

# (b4) FIX-A: junction escape with NON-EXISTENT leaf -> THROW (old code ALLOWED)
if ($junctionMade) {
    $ghostLeaf = Join-Path $escapeJunction 'does-not-exist-yet.bin'
    $threw = $false
    try { Assert-WckPathUnderRoot -Path $ghostLeaf -Root $realRoot }
    catch { $threw = $true }
    Record 'guard.b4 FIX-A junction escape (nonexistent leaf) -> THROW' $threw
} else {
    Record 'guard.b4 FIX-A junction escape (nonexistent leaf) -> THROW' $false 'could not create test junction'
}

# (b5) FIX-A: a non-existent ROOT -> THROW (no lexical fail-open fallback)
$threw = $false
try { Assert-WckPathUnderRoot -Path (Join-Path $scratch 'no-root\child') -Root (Join-Path $scratch 'no-such-root') }
catch { $threw = $true }
Record 'guard.b5 FIX-A nonexistent root -> THROW (no lexical fallback)' $threw

# (b6) FIX-2: linked campaign root resolving elsewhere -> THROW
$linkedRootMade = $false
$linkedRoot = Join-Path $scratch 'linked-campaign-root'
$linkedRootTarget = Join-Path $scratch 'linked-root-outside-target'
New-Item -ItemType Directory -Force -Path $linkedRootTarget | Out-Null
try {
    New-Item -ItemType SymbolicLink -Path $linkedRoot -Target $linkedRootTarget -ErrorAction Stop | Out-Null
    $linkedRootMade = Test-Path -LiteralPath $linkedRoot
} catch { $linkedRootMade = $false }
if ($linkedRootMade) {
    $threw = $false
    try { Assert-WckPathUnderRoot -Path (Join-Path $linkedRoot 'child') -Root $linkedRoot }
    catch { $threw = $true }
    Record 'guard.b6 FIX-2 linked campaign root -> THROW' $threw
} else {
    Record 'guard.b6 FIX-2 linked campaign root -> THROW' $false 'could not create test symlink'
}

# (b7) FIX-2: two-hop cyclic symlink under a real root -> THROW
$cycleMade = $false
$cycleA = Join-Path $realRoot 'cycle-a'
$cycleB = Join-Path $realRoot 'cycle-b'
try {
    New-Item -ItemType SymbolicLink -Path $cycleA -Target $cycleB -ErrorAction Stop | Out-Null
    New-Item -ItemType SymbolicLink -Path $cycleB -Target $cycleA -ErrorAction Stop | Out-Null
    $cycleMade = (Test-Path -LiteralPath $cycleA) -and (Test-Path -LiteralPath $cycleB)
} catch { $cycleMade = $false }
if ($cycleMade) {
    $threw = $false
    try { Assert-WckPathUnderRoot -Path (Join-Path $cycleA 'leaf') -Root $realRoot }
    catch { $threw = $true }
    Record 'guard.b7 FIX-2 two-hop cyclic symlink -> THROW' $threw
} else {
    Record 'guard.b7 FIX-2 two-hop cyclic symlink -> THROW' $false 'could not create test symlinks'
}

# (c) marker'd VM + GENUINELY in-root path -> BOTH pass (no throw)
$ok = $false
try {
    $g = Assert-WckDisposableVM -VMName 'WCK-E2E' -VMObject ([pscustomobject]@{ Name = 'WCK-E2E'; Notes = "campaign cell`r`nWCK-CAMPAIGN:$fakeGuid`r`nok" })
    $inRoot = Join-Path $realRoot 'run-xyz\evidence'   # a true descendant (no link)
    $p = Assert-WckPathUnderRoot -Path $inRoot -Root $realRoot
    $ok = ($g -eq $fakeGuid) -and ($p -like (Join-Path $realRoot '*'))
} catch { $ok = $false }
Record 'guard.c marker + genuinely-in-root path -> PASS' $ok ("guid=$fakeGuid")

# (c2) Assert-WckCampaignReady with injected fixtures -> passes
$ok = $false
try {
    $r = Assert-WckCampaignReady -VMName 'WCK-E2E' -Checkpoint 'baseline-clean' `
            -VMObject ([pscustomobject]@{ Name = 'WCK-E2E'; Notes = "WCK-CAMPAIGN:$fakeGuid" }) `
            -Checkpoints @([pscustomobject]@{ Name = 'baseline-clean' })
    $ok = $r.Ready -and ($r.CampaignGuid -eq $fakeGuid)
} catch { $ok = $false }
Record 'guard.c2 Assert-WckCampaignReady (fixtures) -> PASS' $ok

# =========================================================================================
# F0.5b PERSONA MANIFESTS + CLONE GUARD
# =========================================================================================
Write-Host ''
Write-Host '-- F0.5b persona manifests + VM2 clone guard --' -ForegroundColor Cyan

function Test-PersonaManifestShape {
    param([string] $Path, [string] $ExpectedPersona)

    try {
        $j = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop
        if ([string]$j.persona -ne $ExpectedPersona) { return "persona mismatch" }
        if ([int]$j.expectedAdapterCount -ne 0) { return "expectedAdapterCount != 0" }
        if (@($j.apps).Count -lt 1) { return "apps empty" }
        if (@($j.seeds).Count -lt 1) { return "seeds empty" }
        foreach ($app in @($j.apps)) {
            foreach ($field in 'id','installer','kind','uninstallExpectation') {
                if (-not $app.PSObject.Properties.Match($field).Count -or [string]::IsNullOrWhiteSpace([string]$app.$field)) {
                    return "app missing $field"
                }
            }
            if ([string]$app.installer -ne 'synthetic-seed' -and [string]$app.installer -notlike 'F:\WCK-VM\installers\*') {
                return "installer outside F:\WCK-VM\installers"
            }
        }
        foreach ($seed in @($j.seeds)) {
            foreach ($field in 'type','path','syntheticContent') {
                if (-not $seed.PSObject.Properties.Match($field).Count -or [string]::IsNullOrWhiteSpace([string]$seed.$field)) {
                    return "seed missing $field"
                }
            }
        }
        return $null
    }
    catch { return $_.Exception.Message }
}

$manifestAReason = Test-PersonaManifestShape -Path (Join-Path $here 'persona-a.json') -ExpectedPersona 'A'
Record 'f05b.manifest persona-a schema-valid -> PASS' ($null -eq $manifestAReason) $manifestAReason
$manifestBReason = Test-PersonaManifestShape -Path (Join-Path $here 'persona-b.json') -ExpectedPersona 'B'
Record 'f05b.manifest persona-b schema-valid -> PASS' ($null -eq $manifestBReason) $manifestBReason

$cloneTarget = Join-Path $realRoot 'vm2-existing'
New-Item -ItemType Directory -Force -Path $cloneTarget | Out-Null
$threw = $false
try { Assert-WckCampaignCloneTarget -VMName 'VM2-NoMarker' -TargetDir $cloneTarget -CampaignRoot $realRoot }
catch { $threw = $true }
Record 'f05b.clone markerless existing target -> THROW' $threw

# =========================================================================================
# VERIFIER TESTS (D2) — including FIX-B/FIX-C non-vacuity
# =========================================================================================
Write-Host ''
Write-Host '-- Verifier (Test-WckCampaignEvidence.ps1) --' -ForegroundColor Cyan

# (a) well-formed FRESH evidence -> Pass=true
$good = Join-Path $scratch 'good'
New-GoodEvidence -Dir $good
$rv = Test-WckCampaignEvidence -EvidenceDir $good -HostUsername '___no_such_user___'
$detailA = if ($rv.Pass) { "digest=$($rv.Digest.Substring(0,12))..." } else { "reasons: $($rv.Reasons -join ' | ')" }
Record 'verify.a well-formed FRESH evidence -> Pass=true' ($rv.Pass -eq $true) $detailA

$goodPersonaB = Join-Path $scratch 'persona-b-uninstall-good'
New-GoodPersonaBUninstallEvidence -Dir $goodPersonaB
$rvB = Test-WckCampaignEvidence -EvidenceDir $goodPersonaB -HostUsername '___no_such_user___'
$detailB = if ($rvB.Pass) { "digest=$($rvB.Digest.Substring(0,12))..." } else { "reasons: $($rvB.Reasons -join ' | ')" }
Record 'verify.persona-b Chrome-present + qBittorrent-present honest-decline -> Pass=true' ($rvB.Pass -eq $true) $detailB

# Helper to build a broken variant from the good fixture, mutate it, and expect Pass=false.
function Test-Broken {
    param([string] $Name, [scriptblock] $Mutate)
    $dir = Join-Path $scratch ("broken-" + ($Name -replace '[^a-zA-Z0-9]','_'))
    New-GoodEvidence -Dir $dir
    & $Mutate $dir
    $r = Test-WckCampaignEvidence -EvidenceDir $dir -HostUsername '___no_such_user___'
    $expected = ($r.Pass -eq $false)
    $reason = if ($r.Reasons.Count) { $r.Reasons[0] } else { '(no reason!)' }
    Record ("verify.broken $Name -> Pass=false") $expected $reason
}

function Test-BrokenPersonaB {
    param([string] $Name, [scriptblock] $Mutate)
    $dir = Join-Path $scratch ("persona-b-broken-" + ($Name -replace '[^a-zA-Z0-9]','_'))
    New-GoodPersonaBUninstallEvidence -Dir $dir
    & $Mutate $dir
    $r = Test-WckCampaignEvidence -EvidenceDir $dir -HostUsername '___no_such_user___'
    $expected = ($r.Pass -eq $false)
    $reason = if ($r.Reasons.Count) { $r.Reasons[0] } else { '(no reason!)' }
    Record ("verify.persona-b broken $Name -> Pass=false") $expected $reason
}

Test-BrokenPersonaB 'qbittorrent-absent' {
    param($d)
    $afterReg = Join-Path $d 'after/uninstall-registry.reg'
    $text = Get-Content -LiteralPath $afterReg -Raw
    $text = $text -replace '(?ms)\r?\n?\[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\qBittorrent\]\r?\n"DisplayName"="qBittorrent"\r?\n"InstallLocation"=""\r?\n?', "`r`n"
    Set-Content -LiteralPath $afterReg -Value $text -Encoding UTF8
}

Test-BrokenPersonaB 'chrome-absent' {
    param($d)
    $afterReg = Join-Path $d 'after/uninstall-registry.reg'
    $text = Get-Content -LiteralPath $afterReg -Raw
    $text = $text -replace '(?ms)\r?\n?\[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\\{CHROME-PERSONA-B\}\]\r?\n"DisplayName"="Google Chrome"\r?\n?', "`r`n"
    Set-Content -LiteralPath $afterReg -Value $text -Encoding UTF8
}

Test-BrokenPersonaB 'chrome-removed-success-claim' {
    param($d)
    $p = Join-Path $d 'uninstall-e2e-evidence.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    $j.executions = @([pscustomobject]@{ targetId = 'chrome-enterprise'; skipped = $false; removedFromRegistry = $true; detail = 'claimed removed' })
    $j | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $p -Encoding UTF8
}

Test-BrokenPersonaB 'chrome-manual-claim' {
    param($d)
    $p = Join-Path $d 'uninstall-e2e-evidence.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    foreach ($f in $j.focus) {
        if ($f.targetId -eq 'chrome-enterprise') { $f.classification = 'MANUAL' }
    }
    $j | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $p -Encoding UTF8
}

Test-BrokenPersonaB 'synthetic-app-claimed-real-installed' {
    param($d)
    $p = Join-Path $d 'persona-b-disposition.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    foreach ($a in $j.apps) {
        if ($a.appId -eq 'discord') { $a.source = 'real-installed' }
    }
    $j | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $p -Encoding UTF8
}

# (b1) registry NOT gone: AFTER snapshot still lists git
Test-Broken 'registry-gone=False' {
    param($d)
    $afterReg = Join-Path $d 'after/uninstall-registry.reg'
    Add-Content -LiteralPath $afterReg -Value "`r`n[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Git_is1]`r`n`"DisplayName`"=`"Git`""
}

# (b1b) registry witness non-vacuity: required-but-not-executed 7zip missing -> FAIL
Test-Broken 'registry-required-7zip-missing' {
    param($d)
    $afterReg = Join-Path $d 'after/uninstall-registry.reg'
    $text = Get-Content -LiteralPath $afterReg -Raw
    $text = $text -replace '(?ms)\r?\n?\[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\\{23170F69-40C1-2702-2409-000001000000\}\]\r?\n"DisplayName"="7-Zip 24\.09 \(x64\)"\r?\n?', "`r`n"
    Set-Content -LiteralPath $afterReg -Value $text -Encoding UTF8
}

# (b2) JSON verdict / process exit-code mismatch
Test-Broken 'JSON-exit mismatch' {
    param($d)
    Set-Content -LiteralPath (Join-Path $d 'harness-exitcode.txt') -Value '1' -Encoding UTF8
}

# (b3) missing required evidence file
Test-Broken 'missing-file' {
    param($d)
    Remove-Item -LiteralPath (Join-Path $d 'vm-final-state.json') -Force
}

# (b4) planted secret-pattern (OBVIOUSLY FAKE placeholder, matches shape only)
Test-Broken 'planted secret-pattern' {
    param($d)
    Set-Content -LiteralPath (Join-Path $d 'leak-note.txt') -Value 'token: ghp_FAKE0000000000000000000000000000FAKE' -Encoding UTF8
}

# (b5) negative-control green
Test-Broken 'negative-control-green' {
    param($d)
    $p = Join-Path $d 'uninstall-e2e-evidence.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    foreach ($f in $j.focus) { if ($f.targetId -in '7zip','notepadpp') { $f.classification = 'ALLOW' } }
    ($j | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $p -Encoding UTF8
}

# (b6) final VM running
Test-Broken 'final-VM-running' {
    param($d)
    @{ state = 'Running'; checkpoint = 'baseline-clean' } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $d 'vm-final-state.json') -Encoding UTF8
}

# ---- NEW FIX-B/FIX-C/FIX-H discriminating cases (the OLD verifier returned Pass=true) ----

# (b7) FIX-B STALE FILE: backdate a required file to 1999 (outside the run window) -> FAIL
Test-Broken 'stale-file-1999' {
    param($d)
    $stale = Get-Date '1999-01-01T00:00:00Z'
    $f = Get-Item -LiteralPath (Join-Path $d 'before/dir-hashes.json')
    $f.LastWriteTimeUtc = $stale
}

# (b7b) F0.5b FRESHNESS: one-hour pre-runStart artifact is stale even inside forward slack -> FAIL
Test-Broken 'artifact-before-runStart' {
    param($d)
    $m = Get-Content -LiteralPath (Join-Path $d 'cell-manifest.json') -Raw | ConvertFrom-Json
    $runStart = ConvertTo-WckUtc ($m.runStartUtc)
    $f = Get-Item -LiteralPath (Join-Path $d 'after/dir-hashes.json')
    $f.LastWriteTimeUtc = $runStart.AddHours(-1)
}

# (b7d) F0.5b FRESHNESS: missing runStartUtc cannot anchor legacy evidence -> FAIL
Test-Broken 'missing-runStartUtc' {
    param($d)
    $p = Join-Path $d 'cell-manifest.json'
    $m = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    $m.PSObject.Properties.Remove('runStartUtc')
    ($m | ConvertTo-Json) | Set-Content -LiteralPath $p -Encoding UTF8
}

# (b7e) F0.5b ORDERING: sub-second after<reset must remain distinguishable -> PASS
$subSecondDir = Join-Path $scratch 'sub-second-ordering'
New-GoodEvidence -Dir $subSecondDir
$subSecondManifestPath = Join-Path $subSecondDir 'cell-manifest.json'
$subSecondManifest = Get-Content -LiteralPath $subSecondManifestPath -Raw | ConvertFrom-Json
$subSecondStart = ConvertTo-WckUtc ($subSecondManifest.runStartUtc)
$subSecondManifest.afterSnapshotUtc = $subSecondStart.AddSeconds(2.0).ToString('o')
$subSecondManifest.resetUtc = $subSecondStart.AddSeconds(2.3).ToString('o')
($subSecondManifest | ConvertTo-Json) | Set-Content -LiteralPath $subSecondManifestPath -Encoding UTF8
$rSubSecond = Test-WckCampaignEvidence -EvidenceDir $subSecondDir -HostUsername '___no_such_user___'
$detailSubSecond = if ($rSubSecond.Pass) { 'afterSnapshotUtc precedes resetUtc by 0.3s' } else { "reasons: $($rSubSecond.Reasons -join ' | ')" }
Record 'verify.f05b sub-second after<reset ordering -> Pass=true' ($rSubSecond.Pass -eq $true) $detailSubSecond

# (b7c) FIX-3 STRICT JSON BOOL: string booleans must fail, not coerce truthy/falsey -> FAIL
Test-Broken 'string-json-booleans' {
    param($d)
    $p = Join-Path $d 'uninstall-e2e-evidence.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    $j.pass = 'true'
    foreach ($e in $j.executions) {
        if ($e.targetId -eq 'git') { $e.removedFromRegistry = 'true' }
    }
    ($j | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $p -Encoding UTF8
}

# (b8) FIX-B MALFORMED HASH JSON: garbage in before/dir-hashes.json -> FAIL
Test-Broken 'malformed-hash-json' {
    param($d)
    Set-Content -LiteralPath (Join-Path $d 'before/dir-hashes.json') -Value '{ this is : not valid json ]' -Encoding UTF8
}

# (b8b) FIX-B HASH SHAPE: valid JSON but Sha256 not a 64-hex digest -> FAIL
Test-Broken 'hash-bad-sha-shape' {
    param($d)
    @([pscustomobject]@{ Path = 'C:\Program Files\Git\git.exe'; Sha256 = 'NOT-A-HASH' }) |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $d 'before/dir-hashes.json') -Encoding UTF8
}

# (b8c) F0.5b FILE HASHES: git files in AFTER dir-hashes no longer decide Uninstall truth.
$hashAfterDir = Join-Path $scratch 'hash-after-not-ground-truth'
New-GoodEvidence -Dir $hashAfterDir
@([pscustomobject]@{ Path = 'C:\Program Files\Git\git.exe'; Sha256 = 'A1B2C3D4E5F600000000000000000000000000000000000000000000000000AA' }) |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $hashAfterDir 'after/dir-hashes.json') -Encoding UTF8
$rHashAfter = Test-WckCampaignEvidence -EvidenceDir $hashAfterDir -HostUsername '___no_such_user___'
$detailHashAfter = if ($rHashAfter.Pass) { 'registry witness clean' } else { "reasons: $($rHashAfter.Reasons -join ' | ')" }
Record 'verify.f05b after dir-hashes not Uninstall ground-truth -> Pass=true' ($rHashAfter.Pass -eq $true) $detailHashAfter

# (b9) FIX-C ORDERING: afterSnapshotUtc >= resetUtc -> FAIL
Test-Broken 'after>=reset ordering' {
    param($d)
    $p = Join-Path $d 'cell-manifest.json'
    $m = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    $now = (Get-Date).ToUniversalTime()
    $m.afterSnapshotUtc = $now.ToString('o')             # AFTER now LATER than reset
    $m.resetUtc         = $now.AddSeconds(-60).ToString('o')
    ($m | ConvertTo-Json) | Set-Content -LiteralPath $p -Encoding UTF8
}

# (b10) FIX-B RESULT.TXT random text instead of a banner -> FAIL
Test-Broken 'result-txt-random' {
    param($d)
    Set-Content -LiteralPath (Join-Path $d 'uninstall-e2e-result.txt') -Value 'lorem ipsum random garbage' -Encoding UTF8
}

# (b11) F0.5b PRE-STATE: empty BEFORE dir-hashes is legal and not a missing file.
$emptyBeforeDir = Join-Path $scratch 'empty-before-hashes'
New-GoodEvidence -Dir $emptyBeforeDir
Write-WckDirHashesJson -Path (Join-Path $emptyBeforeDir 'before/dir-hashes.json') -Hashes @()
$rEmptyBefore = Test-WckCampaignEvidence -EvidenceDir $emptyBeforeDir -HostUsername '___no_such_user___'
$emptyContent = (Get-Content -LiteralPath (Join-Path $emptyBeforeDir 'before/dir-hashes.json') -Raw).Trim()
$detailEmptyBefore = if ($rEmptyBefore.Pass) { "content=$emptyContent" } else { "content=$emptyContent reasons: $($rEmptyBefore.Reasons -join ' | ')" }
Record 'verify.f05b empty before dir-hashes [] -> Pass=true' (($emptyContent -eq '[]') -and ($rEmptyBefore.Pass -eq $true)) $detailEmptyBefore

# (b12) FIX-B EXTRA ARTIFACT MOVES THE DIGEST (and the new file is also scanned).
#       Build two good fixtures; add a benign extra file to one; digests must DIFFER.
$digA = Join-Path $scratch 'digest-base'
$digB = Join-Path $scratch 'digest-extra'
New-GoodEvidence -Dir $digA
New-GoodEvidence -Dir $digB
Set-Content -LiteralPath (Join-Path $digB 'extra-benign.txt') -Value 'an extra accepted artifact' -Encoding UTF8
$rA = Test-WckCampaignEvidence -EvidenceDir $digA -HostUsername '___no_such_user___'
$rB = Test-WckCampaignEvidence -EvidenceDir $digB -HostUsername '___no_such_user___'
$digestMoved = ($rA.Pass -and $rB.Pass -and ($rA.Digest -ne $rB.Digest))
Record 'verify.digest extra artifact MOVES digest (FIX-B)' $digestMoved ("A=$($rA.Digest.Substring(0,8)) B=$($rB.Digest.Substring(0,8))")

# (b13) FIX-B UNREADABLE FILE -> FAIL (fail-closed). Lock a required file with an exclusive
#       handle so the verifier cannot read it; it must FAIL rather than silently pass.
$lockDir = Join-Path $scratch 'unreadable'
New-GoodEvidence -Dir $lockDir
$lockTarget = Join-Path $lockDir 'uninstall-e2e-result.txt'
$fs = $null
try {
    $fs = [System.IO.File]::Open($lockTarget, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $rL = Test-WckCampaignEvidence -EvidenceDir $lockDir -HostUsername '___no_such_user___'
    $lockReason = if ($rL.Reasons.Count) { $rL.Reasons[0] } else { '(no reason)' }
Record 'verify.broken unreadable-file -> Pass=false (FIX-B)' ($rL.Pass -eq $false) $lockReason
}
catch {
    Record 'verify.broken unreadable-file -> Pass=false (FIX-B)' $false "could not lock file: $($_.Exception.Message)"
}
finally { if ($fs) { $fs.Dispose() } }

# =========================================================================================
# F2 MODULE VERIFIER TESTS — Migration + Clean non-vacuity (old stub would pass these)
# =========================================================================================
Write-Host ''
Write-Host '-- F2 Migration/Clean module verifiers --' -ForegroundColor Cyan

$goodMig = Join-Path $scratch 'migration-good'
New-GoodMigrationEvidence -Dir $goodMig
$rMig = Test-WckCampaignEvidence -EvidenceDir $goodMig -HostUsername '___no_such_user___'
$detailMig = if ($rMig.Pass) { "digest=$($rMig.Digest.Substring(0,12))..." } else { "reasons: $($rMig.Reasons -join ' | ')" }
Record 'f2.migration well-formed evidence -> Pass=true' ($rMig.Pass -eq $true) $detailMig

$goodMigB = Join-Path $scratch 'migration-persona-b-good'
New-GoodPersonaBMigrationEvidence -Dir $goodMigB
$rMigB = Test-WckCampaignEvidence -EvidenceDir $goodMigB -HostUsername '___no_such_user___'
$detailMigB = if ($rMigB.Pass) { "digest=$($rMigB.Digest.Substring(0,12))..." } else { "reasons: $($rMigB.Reasons -join ' | ')" }
Record 'f2.migration persona-b honest-warning evidence -> Pass=true' ($rMigB.Pass -eq $true) $detailMigB

function Test-BrokenMigration {
    param([string] $Name, [scriptblock] $Mutate)
    $dir = Join-Path $scratch ("migration-broken-" + ($Name -replace '[^a-zA-Z0-9]','_'))
    New-GoodMigrationEvidence -Dir $dir
    & $Mutate $dir
    $r = Test-WckCampaignEvidence -EvidenceDir $dir -HostUsername '___no_such_user___'
    $expected = ($r.Pass -eq $false)
    $reason = if ($r.Reasons.Count) { $r.Reasons[0] } else { '(no reason!)' }
    Record ("f2.migration broken $Name -> Pass=false") $expected $reason
}

function Test-BrokenPersonaBMigration {
    param([string] $Name, [scriptblock] $Mutate)
    $dir = Join-Path $scratch ("migration-persona-b-broken-" + ($Name -replace '[^a-zA-Z0-9]','_'))
    New-GoodPersonaBMigrationEvidence -Dir $dir
    & $Mutate $dir
    $r = Test-WckCampaignEvidence -EvidenceDir $dir -HostUsername '___no_such_user___'
    $expected = ($r.Pass -eq $false)
    $reason = if ($r.Reasons.Count) { $r.Reasons[0] } else { '(no reason!)' }
    Record ("f2.migration persona-b broken $Name -> Pass=false") $expected $reason
}

Test-BrokenMigration 'restore-SHA-mismatch' {
    param($d)
    $p = Join-Path $d 'migration-e2e-evidence.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    foreach ($v in $j.verifications) {
        if ($v.recipeId -eq 'git.config') {
            $v.shaMatch = $false
            $v.restoredSha = '9999999999999999999999999999999999999999999999999999999999999999'
        }
    }
    ($j | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $p -Encoding UTF8
}

Test-BrokenPersonaBMigration 'discord-token-restore-success-claim' {
    param($d)
    $p = Join-Path $d 'migration-e2e-evidence.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    $j.verifications += [pscustomobject]@{
        recipeId = 'discord'
        relativePath = 'Local State'
        skipped = $false
        skipReason = $null
        shaMatch = $true
        destExists = $true
        manifestSha = '3333333333333333333333333333333333333333333333333333333333333333'
        restoredSha = '3333333333333333333333333333333333333333333333333333333333333333'
        destPath = 'C:\MigE2E\B\AppData\Roaming\discord\Local State'
    }
    $j | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $p -Encoding UTF8
}

Test-BrokenMigration 'discord-token-restore-success-claim' {
    param($d)
    $p = Join-Path $d 'migration-e2e-evidence.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    $j.verifications += [pscustomobject]@{
        recipeId = 'discord'
        relativePath = 'Local State'
        skipped = $false
        skipReason = $null
        shaMatch = $true
        destExists = $true
        manifestSha = '3333333333333333333333333333333333333333333333333333333333333333'
        restoredSha = '3333333333333333333333333333333333333333333333333333333333333333'
        destPath = 'C:\MigE2E\B\AppData\Roaming\discord\Local State'
    }
    ($j | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $p -Encoding UTF8
}

Test-BrokenMigration 'zip-secret-entry' {
    param($d)
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zipPath = Join-Path $d 'migration-export.zip'
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.CreateEntry('leak.txt')
        $sw = [System.IO.StreamWriter]::new($entry.Open())
        try { $sw.Write('sk-FAKE0000000000000000000000000000') } finally { $sw.Dispose() }
    }
    finally { $zip.Dispose() }
}

Test-BrokenMigration 'zip-missing-SKILL-md' {
    param($d)
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zipPath = Join-Path $d 'migration-export.zip'
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($entry in @($zip.Entries | Where-Object { $_.FullName -match '(^|/)SKILL\.md$' })) {
            $entry.Delete()
        }
    }
    finally { $zip.Dispose() }
}

$goodClean = Join-Path $scratch 'clean-good'
New-GoodCleanEvidence -Dir $goodClean
$rClean = Test-WckCampaignEvidence -EvidenceDir $goodClean -HostUsername '___no_such_user___'
$detailClean = if ($rClean.Pass) { "digest=$($rClean.Digest.Substring(0,12))..." } else { "reasons: $($rClean.Reasons -join ' | ')" }
Record 'f2.clean well-formed evidence -> Pass=true' ($rClean.Pass -eq $true) $detailClean

function Test-BrokenClean {
    param([string] $Name, [scriptblock] $Mutate)
    $dir = Join-Path $scratch ("clean-broken-" + ($Name -replace '[^a-zA-Z0-9]','_'))
    New-GoodCleanEvidence -Dir $dir
    & $Mutate $dir
    $r = Test-WckCampaignEvidence -EvidenceDir $dir -HostUsername '___no_such_user___'
    $expected = ($r.Pass -eq $false)
    $reason = if ($r.Reasons.Count) { $r.Reasons[0] } else { '(no reason!)' }
    Record ("f2.clean broken $Name -> Pass=false") $expected $reason
}

Test-BrokenClean 'System32-deleted' {
    param($d)
    $p = Join-Path $d 'clean-e2e-evidence.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    foreach ($x in $j.gateDecisions) {
        if ($x.name -eq 'P2-ground-truth') { $x.actualAllowed = $false; $x.pass = $false }
    }
    ($j | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $p -Encoding UTF8
}

Test-BrokenClean 'Run-value-still-present' {
    param($d)
    Add-Content -LiteralPath (Join-Path $d 'after/run-registry.reg') -Value "`r`n`"WCK-CleanE2E-Witness`"=`"C:\\FAKE-WCK-CLEANE2E.exe`""
}

Test-BrokenClean 'protected-key-deleted' {
    param($d)
    $p = Join-Path $d 'clean-e2e-evidence.json'
    $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
    foreach ($x in $j.gateDecisions) {
        if ($x.name -eq 'P3-key-delete-refused') { $x.actualAllowed = $true; $x.pass = $false }
    }
    ($j | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $p -Encoding UTF8
}

# =========================================================================================
# CELL-RUNNER HOST-SAFE TESTS — import-safe helpers only, no VM
# =========================================================================================
Write-Host ''
Write-Host '-- Campaign cell helpers (Invoke-WckCampaignCell.ps1) --' -ForegroundColor Cyan

$oldGuestCred = $env:WCK_GUEST_CRED
try {
    Remove-Item Env:\WCK_GUEST_CRED -ErrorAction SilentlyContinue
    $threw = $false
    try { $null = New-WckGuestCredential }
    catch { $threw = $true }
    Record 'cell.a FIX-5 missing guest credential -> THROW (mock)' $threw

    $threw = $false
    $uninstallScript = Join-Path $here '..\Invoke-WckUninstallRun.ps1'
    $out = & pwsh -NoProfile -File $uninstallScript -VMName '__mock_no_vm__' 2>&1
    $threw = ($LASTEXITCODE -ne 0) -and (($out -join "`n") -match 'Guest credential required')
    Record 'cell.b FIX-5 uninstall runner missing guest credential -> THROW before VM' $threw
}
finally {
    if ($null -ne $oldGuestCred) { $env:WCK_GUEST_CRED = $oldGuestCred }
    else { Remove-Item Env:\WCK_GUEST_CRED -ErrorAction SilentlyContinue }
}

$threw = $false
try {
    $null = Assert-WckCampaignFinalState -VMName 'WCK-E2E' -Checkpoint 'baseline-clean' `
        -VMObject ([pscustomobject]@{ Name = 'WCK-E2E'; State = 'Off' }) -ActualCheckpoint 'dirty-checkpoint'
}
catch { $threw = $true }
Record 'cell.c FIX-4 Off but wrong checkpoint -> THROW (mock)' $threw

$dispatchOk = $true
$dispatchDetail = ''
try {
    $expectedScripts = @{
        Uninstall = 'guest-run.ps1'
        Migration = 'migration-guest-run.ps1'
        Clean = 'clean-guest-run.ps1'
        Install = $null
    }
    foreach ($m in 'Uninstall','Migration','Clean','Install') {
        $spec = Get-WckCampaignModuleSpec -Module $m
        if ([string]$spec.Module -ne $m) { throw "$m module field mismatch" }
        if ($expectedScripts[$m] -ne $spec.GuestScript) { throw "$m script mismatch: $($spec.GuestScript)" }
        if ($m -eq 'Install' -and $spec.LiveExec) { throw "Install must not be live-exec" }
        if ($m -ne 'Install' -and -not $spec.LiveExec) { throw "$m should live-exec" }
    }
    $specB = Get-WckCampaignModuleSpec -Module Uninstall -Persona B
    if ($specB.GuestScript -ne 'guest-run-persona-b.ps1') { throw "Persona-B uninstall script mismatch: $($specB.GuestScript)" }
    if (@($specB.TargetDirs | Where-Object { $_ -match 'qBittorrent|Google\\Chrome|WCK-Persona' }).Count -lt 3) {
        throw "Persona-B target dirs do not include qBittorrent/Chrome/persona seed paths"
    }
}
catch { $dispatchOk = $false; $dispatchDetail = $_.Exception.Message }
Record 'f05b.cell module-dispatch maps every module -> PASS' $dispatchOk $dispatchDetail

Write-Host ''
Write-Host '-- F0.5b screenshot provenance --' -ForegroundColor Cyan

$overlayOk = $false
$overlayDetail = ''
try {
    $png = Join-Path $scratch 'overlay-mock.png'
    Add-Type -AssemblyName System.Drawing
    $bmp = [System.Drawing.Bitmap]::new(320, 120, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::White)
    $g.Dispose()
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $nonce = 'nonce-f05b-square-001'
    $text = New-WckCaptureProvenanceText -Hostname 'vm2' -Persona 'B' -Scenario 'Chrome-ABE' -Nonce $nonce
    $overlay = Add-WckPngTextOverlay -Path $png -Text $text
    $overlayOk = (Test-Path -LiteralPath $png) -and ($overlay.OverlayText -match [regex]::Escape($nonce)) -and ($overlay.OverlayText -match 'vm2\|B\|Chrome-ABE\|')
    $overlayDetail = $overlay.OverlayText
}
catch { $overlayDetail = $_.Exception.Message }
Record 'f05b.screenshot provenance overlay includes nonce in frame (mock) -> PASS' $overlayOk $overlayDetail

# =========================================================================================
# SUMMARY
# =========================================================================================
Write-Host ''
Write-Host '=== SUMMARY ===' -ForegroundColor Cyan
$failed = @($results | Where-Object { -not $_.Pass })
$total = $results.Count
Write-Host ("  {0}/{1} assertions matched expectation." -f ($total - $failed.Count), $total)

# Cleanup scratch fixtures (only our own temp dir; host untouched). Remove the junction first
# and symlink fixtures first so Remove-Item never follows or loops through a linked target.
try { if (Test-Path -LiteralPath $escapeJunction) { (Get-Item -LiteralPath $escapeJunction -Force).Delete() } } catch {}
foreach ($link in @($linkedRoot, $cycleA, $cycleB)) {
    try { if ($link -and (Test-Path -LiteralPath $link)) { (Get-Item -LiteralPath $link -Force).Delete() } } catch {}
}
Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue

if ($failed.Count -gt 0) {
    Write-Host '  SELF-TEST FAILED:' -ForegroundColor Red
    foreach ($f in $failed) { Write-Host "    - $($f.Case): $($f.Detail)" -ForegroundColor Red }
    exit 1
}
Write-Host '  ALL F0.5a SELF-TEST ASSERTIONS PASSED.' -ForegroundColor Green
exit 0
