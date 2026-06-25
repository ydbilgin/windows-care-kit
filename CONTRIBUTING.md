# Contributing to Windows Care Kit

Thanks for your interest in contributing. Issues and pull requests are welcome.

Because this app performs **destructive, system-level operations**, contributions are reviewed
through a **safety-first** lens. The bar is higher than a typical utility: a bug here can delete the
wrong file or leak a credential. This guide explains how to work within that model.

## Prerequisites

- **.NET 10 SDK**
- **Windows 10 or 11** (the app is Windows-native; some tests are Windows-only)
- A Git client

## Build & test

```powershell
git clone https://github.com/ydbilgin/windows-care-kit.git
cd windows-care-kit
dotnet build WindowsCareKit.slnx -c Debug
dotnet test  tests/Suite.Tests/Suite.Tests.csproj -c Debug
```

The default test run is **host-safe** — it excludes the `Destructive` category, which is designed
to run **only inside a throwaway Windows Sandbox** (see `sandbox/`), never against your real
machine:

```powershell
# Host-safe (what CI runs):
dotnet test tests/Suite.Tests/Suite.Tests.csproj --filter "Category!=Destructive"
```

A green PR means: **build 0 warnings / 0 errors** and the host-safe suite passing.

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

## The non-negotiable rules

These are enforced by design and by the analyzer; a PR that breaks them will not be merged:

1. **Destructive code lives only in the sanctioned execution layer.** A Banned-APIs analyzer
   **fails the build** if `File.Delete`, registry deletes, process/service kills, etc. are used
   anywhere else. Don't suppress it — route the action through the gate + an adapter.
2. **Everything destructive goes through the single `SafetyGate`** and is re-validated **at
   execution time** (TOCTOU-safe). No side doors, no "trusted caller" bypass.
3. **Dry-run + explicit approval first.** Nothing destructive runs until the user sees a typed,
   risk-classified plan and approves it.
4. **Never fake success.** If a protective step can't be performed (e.g. a restore point when
   System Restore is off), refuse and record a failure — do not report a success that didn't
   happen.
5. **Never copy secrets.** Credential/token/DPAPI files must stay out of backups; the secret filter
   is forbidden-first and must not be weakened.
6. **The UI never says "safe."** Risk language is honest ("risk found / not found"), never a naked
   green "this is safe."

## Tests

- **New behavior needs tests.** Bug fixes should add a regression test.
- **Use fakes and synthetic data — never real personal data**, real credentials, or real machine
  state. The test suite ships in-memory fakes (e.g. `FakeRecipeFileSystem`) for exactly this.
- **Tests must be non-vacuous.** A test that "passes" only because the thing under test was skipped
  is treated as a failure. If a test depends on an environment capability (elevation, System
  Restore, a sandbox), branch on the *real* capability and assert the production-correct outcome in
  each branch.
- Genuinely destructive proof tests belong in the **`Destructive` category** and run in the sandbox
  harness, not on the host.

## Proposing changes

- **Small fix or doc change?** Open a PR directly.
- **New feature or anything touching the safety core / execution layer / path handling / secret
  exclusion?** Open an **issue first** to discuss the approach — these areas get extra scrutiny.
- Keep PRs focused; match the **style and comment density of the surrounding code**.
- Write a clear PR description: what changed, why, how you verified it (paste the build/test
  result), and any safety implications.

## Commit & PR hygiene

- One logical change per commit where practical; clear, imperative commit subjects.
- Don't commit personal data, binaries, secrets, or anything under a `payload/`-style local folder
  (these are git-ignored on purpose).
- Don't disable security tooling (the analyzer, the gate checks, gitleaks) to make a change pass.

## Security issues

**Do not** open a public issue for a vulnerability. See [`SECURITY.md`](SECURITY.md) for private
reporting.

---

By contributing, you agree your contributions are licensed under the project's [MIT](LICENSE)
license.
