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
- **Secret/cache exclusion is real**: seeded `id_rsa` / `*.secret` / `Cache` files placed
  inside a backed-up subtree are proven **absent** from the package (the check fails the
  run if any leaks).

> Honest status (2026-06-21): Layer 1 is automated + green. The Layer 2 **live** real-app
> run is owner-run-when-ready — the harness logic is independently verified over temp, and
> the sandbox scripts are hardened (no UAC self-elevation, download timeouts, live progress
> log). A completed live run's evidence can be appended here as a dated, hashed record.
