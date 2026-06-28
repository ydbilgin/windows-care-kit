# WCK Uninstall E2E — Hyper-V autonomous harness

Real install + real uninstall of four program *kinds* against a clean, disposable,
network-isolated Windows 11 VM, driven entirely over **PowerShell Direct** (no GUI,
no synthetic input, no host UAC). This is the Hyper-V successor to the Windows
Sandbox runner (`sandbox/uninstall-e2e-*.{wsb,cmd}`): same harness + same production
`SafetyGate`, but fully scriptable and concurrent.

## Why Hyper-V (vs Windows Sandbox)
Sandbox is single-instance, GUI-only (no programmatic guest control), and synthetic
mouse input doesn't forward into its remote session — so a modal dialog could stall
an unattended run. Hyper-V + PowerShell Direct gives: programmatic guest exec, file
push over the VMBus, checkpoint reset, and no interactive UAC.

## Files
| File | Runs where | Needs |
|------|-----------|-------|
| `autounattend.xml` | inside Win11 Setup | — |
| `Build-WckVM.ps1` | host, **elevated**, **once** | Administrator |
| `guest-run.ps1` | inside the guest (pushed in) | — |
| `Invoke-WckUninstallRun.ps1` | host, per run | **Hyper-V Administrators** (not full admin) |

## Prerequisites (already satisfied / completed during setup)
- `Microsoft-Hyper-V-All` enabled + rebooted (owner).
- Eval ISO at `F:\WCK-VM\Win11-Ent-Eval-25H2-x64.iso` (verified SHA256, build 26200.6584).
- Installers pre-downloaded to `F:\WCK-VM\installers\` (`7z.msi`, `git.exe`, `npp.exe`, `vscode.exe`).

## One-time setup (OWNER, elevated)
The current build/setup session is **not** elevated and **not** in *Hyper-V Administrators*,
so it cannot create or drive a VM yet. Two things, both one-time:

1. **Grant per-run autonomy** — add yourself to *Hyper-V Administrators* so the
   *non-elevated* per-run harness can drive Hyper-V. (Build-WckVM can do this with
   `-AddHyperVAdmin`, or run standalone, elevated):
   ```powershell
   $grp = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-578')).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
   Add-LocalGroupMember -Group $grp -Member 'ydbil'
   ```
   Group membership only applies to a **new logon session** → after this, **sign out
   and back in** (no reboot needed), then restart your terminal/session.

2. **Build the VM** (elevated PowerShell; ~15–30 min unattended install):
   ```powershell
   pwsh -File sandbox\hyperv\Build-WckVM.ps1 -AddHyperVAdmin
   ```
   This formats a FAT32 answer disk, creates a Gen2 + vTPM + Secure Boot VM with **no
   network adapter** (clean offline local-account OOBE), boots it, waits for the
   desktop, and takes the `baseline-clean` checkpoint. If it ever stalls, peek with
   `vmconnect.exe localhost WCK-E2E` (read-only look; closing the window is fine).

## Per-run (autonomous — after sign out/in)
From a normal (Hyper-V-Admin) session:
```powershell
pwsh -File sandbox\hyperv\Invoke-WckUninstallRun.ps1 -Publish
```
Restores `baseline-clean` → boots → pushes harness+installers over the VMBus →
installs the 4 KINDs → runs `UninstallE2E.exe` (plans, production-gates, **actually
uninstalls git+vscode**, re-reads the registry to prove gone) → pulls evidence to
`C:\WCK-UninstallOutput\` → restores the baseline (discards the dirty state).

Expected per-program verdicts (same as the Sandbox run):
`7-Zip → BLOCK` (msiexec `/I` maintenance verb) · `Git → ALLOW (InstallLocation anchor)` ·
`VS Code → ALLOW (non-elevated)` · `Notepad++ → MANUAL`; git+vscode removed and
**registry-gone**.

## Safety
Disposable, network-isolated VM only. The host's real programs / registry / profile
are never touched, and **no reboot** is issued by these scripts. The guest credential
is a throwaway VM-only password, not a project secret. Per the disposable-machine
guard, `UninstallE2E.exe` refuses `--execute` unless the in-guest opt-in
(`WCK_DISPOSABLE_MACHINE=1` + `%TEMP%\wck-disposable.marker`) is present — it can
never run a real uninstaller on a normal host.
