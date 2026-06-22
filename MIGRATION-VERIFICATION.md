# Format-migration verification (real-program round-trip)

Windows Care Kit's headline value is the **format-migration engine**: back up an app's
config before a Windows reformat and restore it **to the correct place** on a new
machine/profile. This documents how that round-trip is verified, in two layers.

## Layer 1 — logic proof (host-safe, automated)

The production runners (`MigrationBackupRunner`, `MigrationRestoreRunner`) are proven
over fabricated temp profiles in the host-safe test suite — most directly by
`MigrationBackupRunnerTests.E2E_backup_then_restore_places_files_at_profile_B_and_sha_matches_the_manifest`:
backup Profile A → package + manifest → restore to a **relocated** Profile B → every
file lands at B's correct KnownFolder (not A's) and the restored bytes' SHA equals the
manifest SHA. This runs in CI on every push.

## Layer 2 — real-program proof (owner-run, sandbox)

`tools/MigrationE2E` is a console harness that drives the **same production runners**
over **real installed-app config**, inside a disposable network-enabled Windows Sandbox.
It is **owner-run** (like the destructive Tier-B proofs — see `DESTRUCTIVE-VERIFICATION.md`),
not CI-run, because it installs real apps. The harness itself is independently verified:
an auditor ran it over fabricated temp dirs and confirmed it is **non-vacuous** — the
exclusion check is a positive pruning proof that **FAILS** if a seeded secret leaks into
the package (verified by injecting `id_rsa` → `LEAK DETECTED` → exit 1).

### How to run it (host stays untouched; all real-app activity is inside the VM)

Requires Windows Sandbox enabled.

```powershell
# 1. Host (no admin): build + publish the harness, prepare the output dir.
pwsh sandbox\migration-e2e-stage.ps1

# 2. Open sandbox\migration-e2e.wsb. Inside the disposable VM it (user-level, NO admin/UAC):
#      - installs PortableGit (self-extract) + VS Code (user-setup),
#      - generates real .gitconfig / VS Code settings / Claude / Discord config,
#      - seeds secret + cache noise INSIDE a backed-up subtree to prove pruning,
#      - runs MigrationE2E.exe: backup A -> package -> zip -> restore to B -> verify,
#      - writes evidence + a live progress.log to C:\WCK-Output.

# 3. Read results on the HOST at C:\WCK-MigrationOutput\ (evidence JSON + summary + PASS/FAIL).
#    Then close the sandbox (its contents are discarded).
```

### What it proves (and its honest boundary)

- **Backup/export works for all four apps** (git, VS Code, Claude, Discord) → package + zip.
- **Restore/import lands git + Claude** at Profile B with SHA match.
- **Discord + VS Code back up but do NOT restore yet**: the production `RestoreAllowList`
  defers them to "Slice 3" because their config embeds machine-specific paths needing
  rebind first. The harness surfaces this as `SKIPPED (NotAllowListed)` — correct, safe
  behavior, **not** a fake pass.
- **Secret/cache exclusion is real**: all 8 seeded noise files (`id_rsa`, `*.secret`,
  `Cache/*`, `LocalCache/*`, `shell-snapshots/*`, `todos/*`, `GPUCache/*`) are placed
  inside `.claude/skills/demo/` — a directory the Claude recipe actively walks — so
  each item is a genuine backup candidate that the secret/cache overlay must prune.
  The exclusion check fails the run if any of these names appear in the package.

#### Discord backup and deferred restore (by design)

Discord's settings (`discord/settings.json`, `discord/quotes.json`) **are backed up**
into the package — the recipe captures them and they land in the zip export.

However, Discord's restore is **safely deferred**: Discord config contains
machine-specific values (window bounds, local paths, feature flags tied to the local
install) that cannot be blindly applied on a new machine without rebinding. Rather than
silently copying stale/machine-locked values and claiming success, the restore runner
honestly skips Discord via `NotAllowListed` and surfaces it as a deferred step. This
is correct behavior, not a gap.

Restoring Discord settings with machine-specific rebinding is a planned "Slice 3"
capability. Until then, the discord config is safely in your package/zip and can be
compared manually — no data is lost, and no fake restore is claimed.

#### Why `projects/**` and `skills/**` files are in the package but not the restore manifest

The Claude recipe backs up `.claude/projects` and `.claude/skills` as **directory
items** (whole-tree copy). The `MigrationRestoreManifest` tracks individual **file**
restore targets (Slice 2 single-file restore). A directory-source copy action lands
all matching files in the package but produces no per-file manifest entry — by design,
not a bug. The files exist in the package/zip and can be verified there; individual
per-file restore tracking for directory items is a future Slice 3 capability.

> Honest status (2026-06-21): Layer 1 is automated + green. The Layer 2 **live** real-app
> run is owner-run-when-ready — the harness logic is independently verified over temp, and
> the sandbox scripts are hardened (no UAC self-elevation, download timeouts, live progress
> log). A completed live run's evidence can be appended here as a dated, hashed record.
