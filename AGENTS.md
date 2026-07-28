# AGENTS.md — working in Windows Care Kit with OpenAI Codex

This repository is developed and maintained with **OpenAI Codex** as the primary coding agent.
Implementation and test authoring run through Codex against a binding spec; the maintainer scopes
each change, directs Codex, reviews the result, and integrates it. This file is the instruction
surface Codex uses when it works here — read it before making changes.

## What Codex does in this project

- **Implementation** — features and fixes are written by Codex from a written spec/brief.
- **Test authoring** — Codex writes the automated tests for every change (the suite is **1,380+
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

## Architecture and design principles

Beyond the non-negotiable rules above, this project follows the owner's global engineering
standard in full: `~/.claude/CLEAN_ARCHITECTURE_REFERENCE.md` (SOLID + DRY/KISS/YAGNI/Law of
Demeter + 21 architectural patterns + a C1-C12 always-run checklist + 108 red-flag items). Read at
least the Core Checklist (its §2) before any implementation task, and the relevant
principle/pattern sections before an architecture-sensitive change or review. That file is
binding and refines — never weakens — the rules in this one.

A same-day audit against this standard found real, tracked gaps: three forbidden project-reference
edges (`Suite.Module.Restore` -> `Suite.Execution`, `Suite.Module.Uninstall` -> `Suite.Execution`,
`Suite.Execution` -> `Suite.Win32`), several multi-responsibility ViewModels, and a few dead/unwired
abstractions — see `.planning/STAGING/SOLID-MODULAR-AUDIT_2026-07-22.md`. Treat these as known debt
to eventually close, not a pattern to copy in new code.

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
| `docs/` | Tracked design assets and screenshots |

## The Deployment Contract (artifact-level rules)

The layout table above describes **source**. This section describes the **artifact**. A change can be
correct in every layer above and still ship broken: `v0.1.2-beta` displayed the wrong version number
and rendered its entire UI as raw i18n keys, and both defects were found by an external reviewer
installing the package — not by 1471 passing tests, and not by the VM render harness, which publishes
and then launches the exe from inside its own publish folder, where sibling resources are always
present by construction.

**Supported launch modes.** Every artifact-level claim must name the mode it was verified under:

1. `ExtractedZip` — the user unzips a release and runs `WindowsCareKit.exe` in place.
2. `InstalledProgramFiles` — the Inno component installer placed it under Program Files. Component
   selection is real: the supported **compact** type ships neither `Modules\` nor `manifests\`
   (`installer/WindowsCareKit.iss:48-51,64-65`), so their absence is a valid state, never corruption.
3. `AliasedShim` — **unsupported, and loud.** A shim or symlink elsewhere (as a winget *portable*
   package creates under `%LOCALAPPDATA%\Microsoft\WinGet\Links\`) makes `AppContext.BaseDirectory`
   resolve to the *link's* directory, so sibling `lang\`, `manifests\` and `Modules\` are absent.
   Because the app publishes as a multi-file apphost, an exe-only link additionally fails natively
   before managed code runs. This mode is documented as unsupported and must fail visibly, never
   silently.

**Rules.**

- Code that reads a file it did not itself create resolves its path through the layout owner. The
  root derives from **resolved process identity only** — never from a marker file, cwd, environment,
  config, or registry. Module loading is code execution: a root chosen by user-writable filesystem
  evidence is an arbitrary-code-execution vector, not a cosmetic concern.
- A missing shipped resource is a **failure that says so** — never an empty collection, a raw i18n
  key, or a silently shorter nav rail. Optional components absent is a different statement from
  resources unreadable, and the UI must not conflate them.
- Any value that appears in both the repo and the shipped binary (version, product name, SHA) is
  **asserted against the produced binary** in `release.yml` — never assumed to have been injected.
- Changing packaging, publish layout, `installer/*.iss`, or `release.yml` means re-running the
  artifact gates. Any change to the artifact *shape* (for example enabling single-file publish) is an
  amendment to this contract and must be recorded here first. "Tests pass" says nothing about the
  artifact.

### The `--verify-layout` artifact gate

The **only** check that runs the released binary against the released layout and reports what the app
itself resolves. Path existence cannot answer this: the shipped `lang\en.json` can be present and still
be unusable, which is exactly how `v0.1.2-beta` rendered raw i18n keys. This is a **stable, public
contract** — `release.yml` and anyone packaging the artifact depend on it, so its exit codes and report
format do not change without amending this section.

**Invocation.** `WindowsCareKit.exe --verify-layout`, run from the extracted/installed folder.

- The exe is **GUI-subsystem**: it has no console of its own, so the caller **must redirect stdout**
  (and ideally stderr) to capture the report. Unredirected, the line is silently discarded and only the
  exit code survives.
- From PowerShell use `Start-Process -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput <file>`.
  The `&` call operator does not wait for a GUI-subsystem process and leaves `$LASTEXITCODE` stale.
- It verifies only; it shows no window, composes no services, and — the load-bearing part — loads **no
  module**, because module loading executes third-party code. It exits in about a second.

**Report.** Exactly one line on stdout, prefixed `WCK-LAYOUT `, with `key=value` tokens:

```text
WCK-LAYOUT status=Ok root="C:\Program Files\WindowsCareKit" lang\en.json=OK Modules=OPTIONAL-PRESENT manifests=OPTIONAL-PRESENT
```

- `status=` — `Ok`, `LayoutUndetermined`, `BaseStringTableMissing`, or `BaseStringTableUnreadable`.
- Required resources report `OK`, `MISSING`, `UNREADABLE`, or `UNKNOWN` (root undeterminable, so nothing
  was checked). Optional inventory reports `OPTIONAL-PRESENT` / `OPTIONAL-ABSENT` / `UNKNOWN` and never
  affects the exit code — a compact install legitimately ships neither `Modules\` nor `manifests\`.
- `detail="…"` carries the preserved cause when there is one. `processDir=`/`baseDir=` appear only when
  the root could not be determined.

**Exit codes.**

| Code | Meaning | Release action |
|---|---|---|
| `0` | The layout is trustworthy and the app may start. | Ship. |
| `2` | The app root could **not** be determined — the two launch identities disagree (link/shim launch) or one is unobtainable. Nothing about the install can be stated. | Fail the gate. |
| `3` | The root is known, but a required shipped resource is missing or unusable (including a `lang\en.json` that is not a JSON object of string values, or that lacks a non-blank value for any string the shell itself renders — `{}` parses and is still an all-raw-key UI). | Fail the gate. |

Treat any non-zero exit as a failed release. Do not collapse `2` and `3`: they name different repairs.

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full contributor process and [`SECURITY.md`](SECURITY.md)
for disclosure.
