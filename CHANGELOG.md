# Changelog

All notable changes to Windows Care Kit are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **BETA NOTICE:** Real-world destructive operations (uninstall, disk clean,
> backup, install) are under supervised testing on dedicated disposable machines.
> Do not run destructive actions on a production system without reviewing the
> safety model first.

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
