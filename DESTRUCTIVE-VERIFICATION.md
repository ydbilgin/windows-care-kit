# Destructive-test verification (Tier B) — owner-run, sandbox-only

Windows Care Kit has a small set of **destructive** tests that exercise the real, system-modifying
sinks end-to-end through the production safety gate:

- **B1** — create + disable + delete a real **scheduled task**
- **B2** — create + stop + delete a real **service**
- **B3** — create + delete a real **HKLM registry** value/key (writes a `.reg` backup first)
- a real **System Restore point** creation, and a real **profile-write restore**

These are marked `[DisposableFact]`: on a normal host they **statically SKIP** (they require an explicit
disposable-machine opt-in), and they run **only inside a throwaway Windows Sandbox** the maintainer
launches locally.

## Why these are NOT in CI (and why we do not fake it)

GitHub-hosted `windows-latest` runners have **no nested virtualization / Hyper-V**, so they **cannot launch
Windows Sandbox**. A CI job therefore genuinely cannot execute these tests.

We deliberately do **not** add a `workflow_dispatch` job that "validates" a maintainer-uploaded `step4.trx`.
Such a job could check the TRX's *contents*, but it **cannot attest that the TRX was produced by running the
tests in Windows Sandbox from the reviewed commit** — environment approval authenticates *approval*, not
*execution*. A green check that does not prove what it appears to prove is exactly the kind of fake-green this
project refuses. (Decision: `.planning/STAGING/DESTRUCTIVE-CI-AND-EXPORT-GATING_DECISION_2026-06-21.md`,
reached via security review.)

A self-hosted runner on a maintainer's machine is also rejected: it expands the trust/attack surface of a
public repo and contradicts the disposable-only boundary. The **only** way to get genuine CI provenance would
be an *ephemeral external* Windows VM/CI provider that runs the harness itself and binds artifacts to the
commit SHA — out of scope today, noted for the future.

So: **the disposable-sandbox run below is the authoritative destructive-test verification.** It is
**self-attested and manually run** — labelled as such, not dressed up as CI.

## How to reproduce (maintainer)

From the repo root, on a host with **Windows Sandbox enabled** (`Containers-DisposableClientVM`):

```powershell
# 1. Build the offline, self-contained sandbox bundle (host-safe; no admin).
pwsh sandbox/step4-stage.ps1

# 2. Launch the throwaway sandbox. It self-elevates inside the VM, flips the disposable opt-in,
#    runs the FULL suite (incl. Category=Destructive), and writes results to C:\WCK-SandboxOutput\.
#    (double-click, or:)
& "$env:WINDIR\System32\WindowsSandbox.exe" sandbox\WindowsCareKit-step4-test.wsb

# 3. After it finishes (~90s), authoritatively validate the result TRX on the host:
pwsh sandbox/step4-gate.ps1 -Trx C:\WCK-SandboxOutput\step4.trx
```

`sandbox/step4-gate.ps1` parses the TRX and **FAILS LOUDLY** unless **B1, B2 and B3 each ran AND passed** —
this closes the vacuous-pass hole (a statically-skipped `[DisposableFact]` still yields a green `dotnet test`
exit code, which proves nothing).

### Scope of the gate (be precise)

The gate's `$Required` set covers **B1/B2/B3 only**. It does **not** assert that the System-Restore-point or
the real-profile-restore disposable tests ran — those are exercised in the same sandbox run and visible in the
TRX, but the gate does not pin them. Do not read a gate PASS as "all Tier-B proofs ran"; read it as "the three
machine-wide destructive sinks (task/service/HKLM) genuinely ran and passed."

## Provenance log (append-only)

Each entry binds a **TRX SHA-256** to a **commit SHA**. An entry is **only meaningful for that commit**: a
later commit that changes destructive test code (`tests/Suite.Tests/Step4/**`) or the harness (`sandbox/**`)
and has **no fresh entry has NO fresh destructive proof**. Append new entries; never edit an entry in place.

| UTC | Commit (run) | Valid through | TRX SHA-256 | Result | Attestation |
|---|---|---|---|---|---|
| 2026-06-21T08:34:45Z | `194a346` | `73d6791` (destructive code + `sandbox/` byte-identical, `git diff 194a346 73d6791 -- tests/Suite.Tests/Step4 sandbox/` empty) | `20FD814808127AC6548D1760E2F81E90B69E0F2131435617A147CDFBD988A676` | **761/761 passed**; `step4-gate.ps1` PASS (B1/B2/B3 ran + passed, `ELEVATED: YES`) | self-attested, manual Windows Sandbox run |

> To extend "valid through" to a newer HEAD without a re-run, the rule is mechanical: if
> `git diff <run-commit> <HEAD> -- tests/Suite.Tests/Step4 sandbox/` is empty, the destructive behavior is
> unchanged and the proof still holds; otherwise, re-run the sandbox and append a fresh entry with the new TRX
> hash. Do not silently carry an old PASS across a destructive-code change.
