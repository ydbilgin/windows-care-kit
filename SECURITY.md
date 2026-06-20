# Security Policy

Windows Care Kit performs **destructive, system-level operations** (uninstalling apps, deleting
files and registry keys, backing up and restoring personal data). User-data safety is the
project's **primary promise**, so security reports are taken seriously and handled with priority.

## Supported versions

This project is **beta** and ships as rolling releases. Only the **latest release** (and the
current `master`) receive security fixes. There are no long-term support branches.

| Version | Supported |
|---|---|
| Latest release / `master` | ✅ |
| Older releases | ❌ |

## Reporting a vulnerability

**Please report security issues privately — do not open a public issue, PR, or discussion for a
vulnerability.**

Preferred channel:

1. **GitHub Private Vulnerability Reporting** — on this repository, go to the **Security** tab →
   **Report a vulnerability**. This keeps the report private and tracked.
2. **Email fallback** — `yasinderyabilgin@gmail.com` (the maintainer address already on every
   commit). Use a clear subject like `[WCK security] …`.

When reporting, please include:

- A description of the issue and its **security impact** (what an attacker/accidental path could
  do — e.g. delete outside the intended scope, leak a secret into a backup, escape a path guard).
- **Steps to reproduce** or a minimal proof-of-concept.
- Affected version / commit, and your environment (Windows 10/11 build).
- Any suggested fix or mitigation, if you have one.

## What to expect

This is maintained by a **solo, unpaid maintainer**, so there is **no guaranteed SLA**. Realistic
expectations:

- **Acknowledgement:** best-effort within a few days.
- **Assessment & fix:** prioritized by severity; user-data-safety issues come first.
- **Coordinated disclosure:** please give a reasonable window to ship a fix before any public
  write-up. Credit is gladly given to reporters who want it.

## Scope

**In scope** (please report):

- Any path where a destructive action runs **outside** the single `SafetyGate` / sanctioned
  execution layer, or bypasses the **dry-run + explicit-approval** flow.
- **Path-guard escapes** — junction/symlink/TOCTOU tricks that let an operation touch a system or
  out-of-scope location.
- **Secret leakage** — credentials, token stores, or DPAPI-protected data being copied into a
  backup despite the secret-exclusion rules.
- **Privilege / elevation** issues, or a "fake success" where the app reports a protective action
  (e.g. a restore point) that did not actually happen.
- Recipe / manifest parsing that escapes its package root or executes attacker-controlled input.

**Out of scope** (expected behavior, not vulnerabilities):

- The **unsigned-binary SmartScreen warning** — the project has no code-signing certificate; the
  published **SHA-256** is the integrity guarantee (see the release page).
- Reports requiring you to already have full Administrator control of the machine to "exploit."
- Findings produced only by scanning a machine or repository **you do not own or lack permission
  to test**.

## How the project defends itself

Some of this is enforced in code, so reviewers and reporters know where to look:

- **One gate, no exceptions.** Every destructive action passes through a single `SafetyGate` and is
  re-validated **again at execution time** (TOCTOU-safe).
- **Build-enforced isolation.** A Banned-APIs analyzer **fails the build** if destructive APIs
  (`File.Delete`, registry deletes, process kills, …) are called outside the sanctioned execution
  layer.
- **Secret exclusion** is applied at copy time (a forbidden-first leaf filter), so a backup cannot
  silently include known credential/token files.
- **Honest failure.** When a protective guarantee can't be met, the app refuses and records a
  failure rather than reporting a success that didn't happen.
- **No runtime network calls / no telemetry.**

Thank you for helping keep Windows Care Kit safe.
