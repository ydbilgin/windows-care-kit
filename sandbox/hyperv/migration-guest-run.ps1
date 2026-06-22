<#
  migration-guest-run.ps1 — runs INSIDE the WCK-E2E guest (over PowerShell Direct).
  Seeds a synthetic Profile A with REAL-shaped config for Claude / Discord / Git /
  VS Code PLUS excluded noise+secrets, then runs the MigrationE2E harness to prove
  the format-migration backup -> package/zip -> restore round-trip end-to-end on a
  real Windows machine (real KnownFolder resolution, GatedExecutor, CopyAdapter, SHA).

  Offline: no installs (config is written directly — the harness's own contract says
  the proof is the round-trip over real config, not the installs). Synthetic Profile A
  (under -Base) keeps the guest's own profile untouched.

  Proves: Claude import+export (restored SHA-match), Discord export (backed up) +
  honest restore-defer (machine-locked, SKIPPED), Git restored, secrets/cache pruned.
#>
[CmdletBinding()]
param(
    [string] $Harness = 'C:\WCK-MigE2E\MigrationE2E.exe',
    [string] $Base    = 'C:\MigE2E',
    [string] $Output  = 'C:\WCK-MigOutput'
)
$ErrorActionPreference = 'Stop'
$A = Join-Path $Base 'A'; $B = Join-Path $Base 'B'; $Pkg = Join-Path $Base 'Pkg'
$appA = Join-Path $A 'AppData\Roaming'; $localA = Join-Path $A 'AppData\Local'
$appB = Join-Path $B 'AppData\Roaming'; $localB = Join-Path $B 'AppData\Local'

Remove-Item $Base -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $A,$appA,$localA,$B,$Pkg,$Output | Out-Null

# --- git (.gitconfig) ---
Set-Content "$A\.gitconfig" "[user]`n    name = Sandbox User`n    email = sandbox@example.com`n[core]`n    autocrlf = true`n[init]`n    defaultBranch = main" -Encoding ascii
# --- VS Code ---
New-Item -ItemType Directory -Force "$appA\Code\User" | Out-Null
Set-Content "$appA\Code\User\settings.json" '{ "editor.fontSize": 14, "workbench.colorTheme": "Default Dark+" }' -Encoding ascii
Set-Content "$appA\Code\User\keybindings.json" '{ "key": "ctrl+shift+p", "command": "workbench.action.showCommands" }' -Encoding ascii
# --- Claude (.claude tree) ---
# CLAUDE.md + settings.json are single-file recipe items -> appear in restore manifest.
# projects/ and skills/ are directory recipe items -> CopyAdapter copies the whole tree
# into the package, but they produce no per-file restore manifest entries (Slice 2 restore
# is single-file only; per-file tracking of directory items is a future Slice 3 capability).
New-Item -ItemType Directory -Force "$A\.claude\projects\demo\memory","$A\.claude\skills\demo" | Out-Null
Set-Content "$A\.claude\CLAUDE.md" '# WCK Demo CLAUDE.md' -Encoding ascii
Set-Content "$A\.claude\settings.json" '{ "theme": "dark", "model": "claude-sonnet" }' -Encoding ascii
Set-Content "$A\.claude\projects\demo\memory\note.md" '# Demo project memory note' -Encoding ascii
Set-Content "$A\.claude\skills\demo\SKILL.md" '# Demo skill' -Encoding ascii
# --- Discord ---
New-Item -ItemType Directory -Force "$appA\discord" | Out-Null
Set-Content "$appA\discord\settings.json" '{ "SKIP_HOST_UPDATE": true, "WINDOW_BOUNDS": {"width":1280,"height":720} }' -Encoding ascii
Set-Content "$appA\discord\quotes.json" '{ "quotes": ["Be excellent to each other."] }' -Encoding ascii
# --- excluded noise + secrets (must be PRUNED from package) ---
# All 8 items are seeded INSIDE .claude/skills/demo/, which the Claude recipe walks as a
# directory item (no include filter), so every item is a real backup candidate — pruning
# is proven by the exclusion check, not vacuously absent from the scan.
Set-Content "$A\.claude\skills\demo\id_rsa" 'FAKE PRIVATE KEY' -Encoding ascii          # SecretGlobOverlay: id_rsa*
Set-Content "$A\.claude\skills\demo\app.secret" 'FAKE APP SECRET TOKEN' -Encoding ascii # SecretGlobOverlay: *secret*
New-Item -ItemType Directory -Force "$A\.claude\skills\demo\Cache","$A\.claude\skills\demo\LocalCache","$A\.claude\skills\demo\shell-snapshots","$A\.claude\skills\demo\todos","$A\.claude\skills\demo\GPUCache" | Out-Null
Set-Content "$A\.claude\skills\demo\Cache\blob.dat" 'cache' -Encoding ascii              # recipe-wide exclude: *Cache*
Set-Content "$A\.claude\skills\demo\LocalCache\temp.dat" 'cache' -Encoding ascii         # recipe-wide exclude: *Cache*
Set-Content "$A\.claude\skills\demo\shell-snapshots\2026-06-21.snap" 'snap' -Encoding ascii # recipe-wide exclude: shell-snapshots
Set-Content "$A\.claude\skills\demo\todos\todo.txt" 'TODO' -Encoding ascii               # recipe-wide exclude: todos
Set-Content "$A\.claude\skills\demo\GPUCache\f_000001" 'cache' -Encoding ascii           # recipe-wide exclude: *Cache*
Set-Content "$A\.claude\skills\demo\GPUCache\data_0" 'gpu' -Encoding ascii               # recipe-wide exclude: *Cache*

Write-Host "[mig-guest] seeded Profile A: Claude/.gitconfig/VSCode/Discord + secret+cache noise"
if(-not (Test-Path $Harness)){ throw "harness not found: $Harness" }
& $Harness --profileA $A --appdataA $appA --localA $localA --profileB $B --appdataB $appB --localB $localB --package $Pkg --output $Output --gitInstall ok --vscodeInstall ok
$rc = $LASTEXITCODE
Write-Host "[mig-guest] MigrationE2E exit code: $rc"
[pscustomobject]@{ ExitCode = $rc }
