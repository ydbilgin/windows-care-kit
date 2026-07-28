# Changelog

All notable changes to Windows Care Kit are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **BETA NOTICE:** Real-world destructive operations (uninstall, disk clean,
> backup, migration/restore, install) are under supervised testing on dedicated disposable machines.
> Do not run destructive actions on a production system without reviewing the
> safety model first.

---

## [Unreleased]

---

## [0.2.0-beta] - 2026-07-29

### Security

- **Store-app (AppX) removal now runs through the safety gate** like every other
  destructive action — typed action, plan hash, explicit approval, and
  re-validation immediately before execution. Previously it bypassed the gated
  pipeline entirely. The raw removal call is now reachable only from the
  sanctioned execution layer and is enforced by the banned-API analyzer, so no
  future code path can reach around it.
- **A recipe can no longer talk the installer into elevating something you
  don't control.** Install metadata that asked for administrator rights while
  pointing at a program resolved from a user-writable folder
  (`%LOCALAPPDATA%`/`%APPDATA%`) is now routed to manual-only, with a
  trusted-root check and a file-ownership check behind it.
- **Protected registry areas are protected downwards, not just upwards.** Keys
  *under* a boot/logon-critical subtree (e.g. `SOFTWARE\Policies\…`) are now
  refused; ordinary app remnants stay removable.
- **Closed a set of swap-the-file-underneath races** between the moment an
  action is approved and the moment it runs: backup now scans and copies through
  a single locked handle instead of opening the file twice, deletion checks every
  parent folder for a junction immediately before deleting, staged writes use
  random, exclusively-created filenames that detect a pre-planted link instead of
  following it, and a service delete re-reads the live service configuration and
  refuses if its program path changed since you approved it.
- **Actions that need an elevated token are now refused up front** (service
  delete, HKLM key/value delete, scheduled-task delete) when the app is not
  running elevated, instead of being approved and then failing opaquely.
- **A registry backup that cannot be locked down is no longer written at all.**
  Failures while applying restrictive permissions used to be swallowed, leaving a
  weakly-protected cleartext backup behind while the delete proceeded anyway.
- **Backup destinations can no longer escape their own root** (`..\` targets) or
  be placed inside the folder being copied, which previously invited unbounded
  recursive copying.
- **The app decides where its own files live from its process identity alone.**
  The folder that program modules are loaded from is never chosen by inspecting
  directory content, so someone who can write a folder cannot decide which code
  this tool runs. A module folder that fails validation is also no longer
  registered before the check completes.
- **The installer targets an administrator-owned location**, so a limited user
  cannot plant a module next to the app for a later elevated run.

### Fixed

- **The About screen showed `0.1.0` no matter which version you installed, and
  the whole window could render raw text like `app.title` instead of real
  labels.** Both were found by an external winget reviewer installing
  `0.1.2-beta` into a clean machine — 1470 passing tests and the VM screenshot
  harness had missed them, because both live in the gap between "the code is
  correct" and "the shipped package is correct." The version now comes from the
  release tag and nowhere else, and the app locates its shipped language files
  from the real executable location, which fixes launching through a winget
  portable alias (a link, whose folder is not the app's folder).
- **If the app cannot load its English text, it now refuses to start and says
  so**, naming the file it looked for — instead of opening a window full of
  internal key names that explains nothing.
- **Releases are now verified against the artifact they produce.** The published
  build is opened back up — in the build folder and again from inside the ZIP —
  and the release fails if the version stamped in the executable, or the shipped
  file layout, does not match what was claimed. A failed asset upload can no
  longer be masked by a later successful step.
- **Migration listed the same application several times** (one row per detection
  recipe). It is now one row per application, with the parts it found expandable
  underneath and a single badge showing the worst case across them.
- **Migration restore reported everything as "Restored" even when it wasn't.**
  The report is now built from what actually executed; failed, blocked, and
  never-run items are shown honestly as not restored.
- **"Couldn't look" no longer reads as "nothing found."** Recycle Bin, startup
  entries, browser extensions and Store-app inventory now distinguish a genuinely
  empty source from one that could not be read, and show a visible caution
  instead of a confident empty list. A failed Recycle Bin query shows no totals
  at all rather than a fake `0 items`.
- **Turkish text handling.** The destructive-confirmation prompt could reject a
  correctly typed Turkish confirmation word, and Uninstall's search could miss
  matches, because both compared text using the system culture rather than the
  language you selected.
- **The interface no longer stays half-English after switching language.** Plan
  preview rows re-render their risk names, action verbs and undo labels live.
- **Preview counts and result counts are no longer the same sentence** — a
  finished backup used to still read "to copy" rather than reporting what was
  actually copied, failed or skipped.
- **Paths can no longer drift out from under a running operation.** Backup,
  Migration, Install and Restore now refuse destination/checkpoint edits while an
  approved run is in flight, and a plan built against a path you have since
  changed is discarded rather than silently executed against the old one.
- **Background failures are no longer lost or fatal.** Long-running screen
  actions observe faults instead of letting them escape to the window and
  potentially close the app, refuse to double-run when clicked twice, and a
  half-finished scan for a screen you navigated away from no longer writes its
  results into the view you are now looking at.
- **A refresh no longer duplicates the uninstall list** when an earlier, slower
  load finishes after a newer one.
- **Recovery data is treated as recovery data.** A corrupt or unreadable install
  checkpoint is no longer used as if it were a fresh start, and a failed read no
  longer overwrites the only history you had left. A migration package that fails
  midway through being written is now recognisable as incomplete rather than
  passing as a valid settings-only package.
- **Cloud-backup status is honest about not knowing.** "Unknown" is no longer
  reported as "not backed up", and an unverified item is treated as protectively
  as an unprotected one when suggesting defaults.
- **Leaked system handles** when opening a link or a folder from the app.

### Changed

- **The app now ships as a folder instead of a single executable.** Extract the
  whole ZIP and run `WindowsCareKit.exe` from it — the surrounding files are
  required. This is what makes per-module installation possible.
- English and Turkish documentation now describe the six workflows that actually
  ship, the new-machine restore flow and the component installer, instead of
  earlier roadmap wording.
- Internal work with no intended behaviour change: several architecture and
  reliability rounds closed layering violations, removed dead scaffolding, gave
  duplicated restore-safety rules a single owner (proven by a test walking all
  1,260 rule combinations), put a measured performance budget on program
  de-duplication, and fixed an intermittent test-suite race. The host-safe test
  suite grew from 1,275 to 1,547 tests over this release.

### Added

- **A real Windows installer with component selection.** Tick the modules you
  want; the ones you don't tick are never written to disk — no hidden files, no
  greyed-out tabs. Re-run the installer later to add a module or drop one. The
  app discovers whichever modules are present on disk at startup and runs
  correctly with any subset.
- **Per-item labels in migration recipes** (schema v4), so what will be copied is
  described in your language rather than by internal identifiers.

### Removed

- Two unused internal subsystems were deleted rather than left in place: a zip
  transport for migration packages that no screen produced or consumed, and a
  machine-lock probe superseded by the current detection evidence. Neither was
  reachable from the app; keeping half-finished code around is a liability, not
  an asset.

---

## [0.1.2-beta] - 2026-07-03

### Changed

- **Complete visual redesign — emerald light/dark theme pair** across every
  screen (Uninstall, Clean, Back up, Migration, Restore, Reinstall, Settings,
  the confirm gate, and the uninstall wizard): DRY-RUN badges on every dry-run
  screen, two-tier evidence rows, right-rail plan summaries, and risk pills
  colored by outcome. Emerald is reserved for genuinely safe/reversible
  actions; **red is reserved for irreversible ones** — beauty never repaints
  risk as safe.
- README screenshots refreshed to the new theme (captured from a clean VM).

### Security

- **Recycle Bin emptying now runs through the safety gate** like every other
  destructive operation (previously it could execute outside the gated
  pipeline).
- **Banned destructive-API analyzer hole closed** — destructive filesystem
  calls outside the execution layer are surfaced again instead of slipping
  past the analyzer.
- **Per-user registry protection scoped to the actual user SID** — other
  users' hives under HKU are always blocked; only the current user's remainder
  is evaluated against the protected tables.
- **Registry-delete rollback backups hardened** — per-value, collision-proof
  backup filenames (high-resolution stamp + unique suffix), a 120-character
  filename cap so deep backup folders cannot exceed Windows path limits, and a
  short identity hash so two keys that sanitize to the same name remain
  distinguishable.

### Fixed

- **Confirm-gate hover honesty** — on the irreversible tier the Approve button
  stayed loud-red at rest but flipped to emerald while hovered (i.e. during
  the click); it now stays loud-red through the whole interaction.
- **Detection honesty for cloud placeholders** — a folder whose files are
  OneDrive-dehydrated placeholders can no longer present as "analyzed clean";
  skipped placeholders are counted and cap the claim at *not analyzed*
  (read-only scans still never hydrate).
- **Detection honesty for unreadable subtrees** — one unreadable subfolder no
  longer marks the whole folder inaccessible; reachable files are still
  sampled and the partial state honestly blocks any "works" claim.

---

## [0.1.1-beta] - 2026-07-02

### Security

- **Backup content-secret scanner** — backup now scans file *contents*, not just
  names, so a token embedded in an innocently-named file (e.g. an API key inside
  `settings.json`) is detected and kept out of the package; enforced with a
  never-read guard on excluded paths.
- **Hardened credential exclusion** — secret exclusion is seeded at the copy
  engine level (forbidden-first, every copy), so no backup caller can bypass it,
  and now covers `auth.json`, `oauth_creds.json`, `.npmrc`, `.env`/`.env.*`,
  `cred_blob*`, and non-RSA SSH keys. Directory path-globs (`sessions/**`,
  `cache/**`, …) now prune whole subtrees instead of being inert.

### Added

- **Settings screen** — language selector plus an About panel (version, MIT
  license, repository and releases links).
- **Dark / Light theme toggle** (restart-to-apply).
- **Multi-language selector** replacing the EN/TR toggle — adding a language is
  data-only (drop a `lang/<code>.json`); the app defaults to English and falls
  back to English for partially-translated languages.
- **New-machine Restore screen** — side-effect-free preview, approved-hash gate
  (mismatch ⇒ zero mutation), and an honest three-disposition report
  (Restored / Reinstall / Manual).
- `--screen <module>` deep-link and `--lang`/`WCK_LANG` override for
  deterministic launch.
- README "How it works" lifecycle diagram.

### Fixed

- **Restore is fail-closed with honest undo** — undo reverts overwritten files
  byte-for-byte and honestly refuses files that restore *created* (it never
  fabricates a revert).
- **Detection truth-repairs** — deterministic dedup (union-find with an identity
  veto), a reproducible content-probe, and a false-green killer path check;
  the "zero false-green" floor is preserved.
- **Front-door polish and full de-Turkification** — English manifest names and
  content, `REPORT.md` output, navigation clipping fix, and Uninstall search.
- **Settings render crash** — read-only localization binding fix plus correct
  selector display.

---

## [0.1.0-beta] - 2026-06-25

### Added

- **Public launch polish** (2026-06-25)
  - General migration engine public framing: 40-recipe detection, honest
    restore/undo preview, and community-governed recipe expectations.
  - WPF Migration screen: read-only scan, honest selectable preview, and **live
    capture** — pick a backup folder, approve the dry-run plan, and selected
    settings are copied there through the existing safety-gated backup engine
    (single execution path; machine-locked items surfaced honestly). The
    new-machine restore flow is the next slice.
  - Public repository readiness: launch README updates, Turkish README,
    contribution templates, security issue routing, and `AGENTS.md` workflow
    guidance.

- **Four application modules**
  - *Sil / Uninstall* — guided program removal.
  - *Temizle / Clean* — disk and artefact cleanup.
  - *Yedekle / Backup* — profile and settings backup.
  - *Kur / Install* — program installation from a recipe.

- **Safety model**
  - Single `SafetyGate` with a gated executor pipeline: every destructive action
    requires dry-run preview → explicit user approval → TOCTOU re-validation
    before execution.
  - Banned-APIs Roslyn analyzer: references to destructive Win32/BCL APIs outside
    the sanctioned executor layer fail the build.

- **Format-migration engine**
  - Recipe-driven backup → restore path: restores data to the correct
    `KnownFolder` on a different machine, not a hardcoded absolute path.
  - Self-describing install-phase package: records winget/npm reinstall plan at
    export time (export-only; no credentials captured).
  - Secret-store exclusion: credential stores and token files are excluded from
    backup copies at copy time.

- **UI**
  - English / Turkish (EN/TR) dual-language interface.

- **Automated tests and CI**
  - ~780 host-safe automated tests; destructive tests are category-gated and
    excluded from CI by default.
  - GitHub Actions CI: build + test on `windows-latest`, gitleaks secret scan,
    coverlet code-coverage summary.
