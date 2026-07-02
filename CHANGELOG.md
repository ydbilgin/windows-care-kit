# Changelog

All notable changes to Windows Care Kit are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **BETA NOTICE:** Real-world destructive operations (uninstall, disk clean,
> backup, install) are under supervised testing on dedicated disposable machines.
> Do not run destructive actions on a production system without reviewing the
> safety model first.

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
