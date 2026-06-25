# Changelog

All notable changes to Windows Care Kit are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **BETA NOTICE:** Real-world destructive operations (uninstall, disk clean,
> backup, install) are under supervised testing on dedicated disposable machines.
> Do not run destructive actions on a production system without reviewing the
> safety model first.

---

## [0.1.0] - Unreleased

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
