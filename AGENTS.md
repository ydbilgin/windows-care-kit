# AGENTS.md — working in Windows Care Kit with OpenAI Codex

This repository is developed and maintained with **OpenAI Codex** as the primary coding agent.
Implementation and test authoring run through Codex against a binding spec; the maintainer scopes
each change, directs Codex, reviews the result, and integrates it. This file is the instruction
surface Codex uses when it works here — read it before making changes.

## What Codex does in this project

- **Implementation** — features and fixes are written by Codex from a written spec/brief.
- **Test authoring** — Codex writes the automated tests for every change (the suite is **1,140+
  tests**, all host-safe by default). New behavior is not "done" until its tests exist and pass.
- **Independent review** — each change goes through a separate, multi-pass review before the
  maintainer merges it; over-claims, safety-rule violations, and missing tests are caught there.
- **Release & maintenance chores** — build/test verification, changelog and doc updates, dependency
  and recipe-catalog hygiene.

The human maintainer owns scoping, final review, and every merge decision.

## Build & test (host-safe)

```powershell
dotnet build WindowsCareKit.slnx -c Debug
dotnet test  WindowsCareKit.slnx -c Debug --filter "Category!=Destructive"
```

A change is acceptable only when the build is **0 warnings / 0 errors** and the **host-safe** suite
is green. Genuinely destructive proof tests live in the `Destructive` category and run **only** in
the throwaway Windows Sandbox harness under `sandbox/` — never against a real machine.

## Non-negotiable rules (enforced by design + the analyzer)

A change that breaks any of these is rejected:

1. **Destructive code lives only in the sanctioned execution layer** (`src/Suite.Execution/`). A
   Banned-APIs analyzer **fails the build** if `File.Delete`, registry deletes, process/service
   kills, etc. appear anywhere else. Route the action through the gate + an adapter — never suppress
   the analyzer.
2. **Everything destructive passes the single `SafetyGate`** and is re-validated **at execution
   time** (TOCTOU-safe). No side doors, no "trusted caller" bypass.
3. **Dry-run + explicit approval first.** Nothing destructive runs until the user sees a typed,
   risk-classified plan and approves it.
4. **Never fake success.** If a protective step can't be performed, refuse and record a failure —
   never report a success that didn't happen. This honesty rule is the product's core promise.
5. **Never copy secrets.** Credential/token/DPAPI files stay out of backups; the secret filter is
   forbidden-first and must not be weakened.
6. **The UI never says "safe."** Risk language is honest ("risk found / not found"), never a naked
   green "this is safe."
7. **Tests must be non-vacuous** and use fakes/synthetic data — never real personal data, real
   credentials, or real machine state. A test that "passes" only because the thing under test was
   skipped is treated as a failure.

## Commit conventions

- Commits are authored by the maintainer (**Yasin Derya Bilgin**); do **not** add AI co-author
  trailers or generated-by signatures.
- One logical change per commit; clear, imperative subjects; paste the build/test result in PRs.
- Never commit secrets, personal data, or anything under a `payload/`-style local folder
  (git-ignored on purpose). Don't disable the analyzer, gate checks, or gitleaks to make a change
  pass.

## Project layout

| Path | What lives here |
|---|---|
| `src/Suite.Core/` | Modules, the safety core, planning, abstractions (no destructive I/O) |
| `src/Suite.Win32/` | Real Windows implementations of the read-only/probe ports |
| `src/Suite.Execution/` | The **sanctioned execution layer** — the *only* place destructive actions run |
| `src/Suite.App.Wpf/` | The WPF UI (EN/TR) |
| `tests/Suite.Tests/` | Automated tests (fakes + synthetic data) |
| `sandbox/` | Throwaway Windows Sandbox harness for the `Destructive` test tier |
| `docs/` | Architecture & security notes |

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full contributor process and [`SECURITY.md`](SECURITY.md)
for disclosure.
