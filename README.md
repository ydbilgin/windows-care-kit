<div align="center">

# 🛡️ Windows Care Kit

**Carry your settings across a Windows reinstall — and get them back in the right place.**

*Open-source, ad-free, radically honest: it tells you what can't transfer instead of faking success. Covers the full format lifecycle: Uninstall · Clean · Backup · Reinstall.*

[![CI](https://github.com/ydbilgin/windows-care-kit/actions/workflows/ci.yml/badge.svg)](https://github.com/ydbilgin/windows-care-kit/actions/workflows/ci.yml) ![status](https://img.shields.io/badge/status-beta-orange) [![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE) ![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6) ![dotnet](https://img.shields.io/badge/.NET-10-512BD4)

![Windows Care Kit — main window](docs/screenshots/_capture/01-sil.png)

</div>

---

## ⚠️ Status — Beta, read this first

All four modules are **implemented**, the build is **clean (0 warnings / 0 errors)**, and the suite passes **1,080+ automated tests**. Every destructive action runs **only** behind a **dry-run preview + your explicit approval** through a single safety gate.

> **🚧 Real-world destructive operations are still undergoing supervised testing.** Treat this as **beta**: always have a separate backup before letting it delete, restore, or migrate on a machine you care about. See [Roadmap](#-roadmap) for what's built vs. planned.

---

## 🤔 What is it?

Windows Care Kit is **one native Windows app** that covers the whole **format / reinstall lifecycle** — the things you normally juggle across three or four separate (often ad-laden, opaque) tools. It is **open source, ad-free, telemetry-free, and auditable**, and it is **honest**: when something *can't* safely transfer (an encrypted password, a cloud-only save), the app tells you instead of pretending.

| Module | TR | What it does |
|---|---|---|
| 🗑️ **Uninstall** | **Sil** | Remove classic + UWP apps, scan & clean leftovers, run the official uninstaller, per-user AppX removal |
| 🧹 **Clean** | **Temizle** | Junk/temp cleanup (to Recycle Bin), startup manager, browser-extension inventory, empty Recycle Bin |
| 💾 **Backup** | **Yedekle** | Manifest-driven backup of the *few things you can't just re-download* before a format |
| 📦 **Install** | **Kur** | Reinstall apps via winget/npm after a format, restore settings with a safe timestamped `.bak` merge |

**Who it's for:** gamers, AI/developer power-users, and everyday people who are about to reinstall Windows and don't want to lose what matters.

---

## 📸 Screenshots

**🗑️ Sil / Uninstall**

![Sil / Uninstall module — app inventory and dry-run removal plan](docs/screenshots/suite-sil.png)

**🧹 Temizle / Clean**

![Temizle / Clean](docs/screenshots/_capture/02-temizle.png)

**💾 Yedekle / Backup**

![Yedekle / Backup](docs/screenshots/_capture/03-yedekle.png)

**📦 Kur / Install**

![Kur / Install](docs/screenshots/_capture/m-kur.png)

**🔒 Dry-run + approval**

A verified screenshot will be added after the first visual pass.

---

## ✨ Features

### 🗑️ Sil — Uninstall
- Read-only inventory of installed **classic (Win32) and UWP/Store** apps.
- Runs the app's **official uninstaller**, then a **leftover-cleanup wizard** for the files/registry keys it leaves behind.
- **Per-user AppX removal** for Store apps.
- Every removal: **dry-run preview → you approve → it runs** (never silent).

### 🧹 Temizle — Clean
- **Junk / temp scan & clean** — removals go to the **Recycle Bin** (recoverable), not a hard delete.
- **Startup manager** — see and disable what launches at boot.
- **Empty Recycle Bin** — behind an explicit confirmation, and logged.
- **Browser-extension inventory** — list what's installed, open its folder.

### 💾 Yedekle — Backup
- **Manifest-driven plan** for the irreplaceable stuff before a format.
- **Tool / payload separation:** re-downloadable apps are **never copied** — only an *install list* is written, so your backup stays small.
- **Secret-store exclusion is enforced:** browser cookies, saved passwords, token stores (`Login Data`, `Local State`, `key4.db`, …) are **not** copied into the backup.
- Produces a human-readable **`RAPOR.md`** (report) and **`MANUAL_TODO.md`** (the things only *you* can do — e.g. re-login somewhere).
- Your personal backup data lives **outside** the app, never in the repo.

### 📦 Kur — Install / Restore
- **winget / npm reinstall plan** with a sensible **restore order** and **checkpoint/resume**.
- **Restore settings after install** — config files are merged **after** the app exists, with a timestamped **`.bak`** so nothing is blindly overwritten.
- **Auth probe** — tells you where you'll need to log in again.

### 💼 Format-migration (Settings Portability)

The Migration screen uses a **recipe-based detection catalog covering 40 applications** to find
portable settings and present a selectable, honest preview of what can be carried to a new Windows
profile.

**Honest deferral for machine-locked settings:** recipes classify settings that cannot be rebound
reliably on another machine as manual or deferred. They are never shown as a successful automatic
restore. The preview explains what was detected, what is eligible, and what still requires manual
work.

**Available today:** recipe-based detection, selection, and the honest restore preview in the WPF
Migration screen. **One-click live restore execution is still being finalized and is not yet
connected; the Restore button remains disabled.**

---

## 🔒 Safety model (non-negotiable)

This is the part most "cleaner" tools get wrong. Here it is the core design:

- **One gate, no exceptions.** Every destructive action passes through a single **`SafetyGate`** (system-folder guards, junction/symlink resolution, protected process/service guards) and is re-validated **again at execution time** (TOCTOU-safe).
- **Dry-run first, always.** Nothing happens until you see a typed, risk-classified plan and **approve** it.
- **Honest interface.** If something can't transfer (DPAPI-encrypted passwords, cloud-only saves), the app **says so** — it doesn't fake success.
- **No telemetry, no analytics, no phone-home.** The app never contacts a server on its own. The only network activity happens when *you* run the Install module — it reinstalls your apps via `winget`/`npm`, and shows you the exact, approved plan before anything downloads.
- **Tool/payload separation + secret exclusion** so a backup never leaks your credentials.
- **Auditable:** a single sanctioned execution layer, an analyzer that **fails the build** if destructive APIs are used outside it, and a redacted **execution log**.

---

## ⬇️ Download & run

1. Download the latest **self-contained, single-file, portable ZIP** from [Releases](https://github.com/ydbilgin/windows-care-kit/releases).
2. **Verify the SHA256** of the ZIP against the value on the release page.
3. Unzip and run — **no installer**, nothing written to system folders.

> **Note:** the build is **unsigned** (this is a free, no-revenue project, so there's no code-signing certificate). Windows **SmartScreen** may warn on first run — this is expected for unsigned apps; the SHA256 check is your integrity guarantee. There is **no auto-updater** — check the Releases page.

---

## 🛠️ Build from source

Requires the **.NET 10 SDK**.

```powershell
git clone https://github.com/ydbilgin/windows-care-kit.git
cd windows-care-kit
dotnet build WindowsCareKit.slnx -c Release
dotnet test  WindowsCareKit.slnx
```

Project layout: `src/` (modules + safety core + execution layer), `tests/` (automated tests), `docs/` (architecture & security notes).

---

## 🤖 Development workflow — built with Codex

Windows Care Kit is developed and maintained with **OpenAI Codex** as the primary coding agent.
Each change starts from a written spec; **Codex writes the implementation and the automated tests**
(the suite is **1,080+ tests**, host-safe by default), and every change goes through an independent,
multi-pass review before the maintainer merges it. Codex also handles the routine maintainer chores
— build/test verification, changelog and doc updates, and recipe-catalog hygiene.

This is deliberate for a tool that performs **destructive, system-level operations**: the same
discipline the app promises its users (spec → review → never fake success) is applied to its own
development. The agent's working rules live in [`AGENTS.md`](AGENTS.md); the human maintainer owns
scoping, final review, and every merge.

---

## 🗺️ Roadmap

**Built today (beta):** the four modules above, the safety gate + gated executor, EN/TR UI, automated test suite.

**Designed & planned (not in this build yet):** a richer Backup/Restore engine —
- 🔎 **Auto-discovery catalog** of local app settings & dev/AI-CLI configs (Codex/Discord/VS Code…), with a checkbox selection screen + manual-path add.
- 🖥️ **Machine-aware restore** — abstracts the source/target machine (user profile, drive letters, known-folders) so a backup actually works on a *different* PC.
- 💽 **Multi-drive scan** (not just C:), with cloud-redundancy detection (skip what Steam Cloud / OneDrive already holds).
- 📋 **Package inventory** — capture *what's installed* in pip/npm/winget (the list, not the files) and reinstall it.
- 📥 **Import / "recovery profile"** — portable selection profile + optional auto-install of missing apps.
- 🎮 **Optional game-file backup** (Steam/Epic), with honest platform limits (Xbox/Game Pass = reinstall-only).

See `docs/` for the full design decisions.

---

## 🤝 Contributing

Issues and PRs welcome. Because this app performs **destructive, system-level operations**, contributions are reviewed with that in mind:
- Destructive code lives **only** in the sanctioned execution layer; the analyzer enforces this.
- New behavior needs tests; tests use **fakes/synthetic data**, never real personal data.
- See [`CONTRIBUTING.md`](CONTRIBUTING.md) and [`SECURITY.md`](SECURITY.md) for the development & disclosure process.

## 🔐 Security

Found a security issue? Please report it privately (see [`SECURITY.md`](SECURITY.md)) rather than opening a public issue. This project treats user-data safety as its primary promise.

## 🕵️ Privacy

No telemetry, no analytics, no phone-home — the app never contacts a server on its own. The only network activity is when *you* run the Install module, which reinstalls your apps via `winget`/`npm`; it shows you the exact, approved plan before anything downloads. Your backup data (`payload/`) never enters the repository and never leaves your machine unless *you* move it.

## 🌍 Language

UI ships in **English and Turkish (EN/TR)**. See the [Türkçe README](README.tr.md).

## 📄 License

[MIT](LICENSE).

---

<div align="center">
<sub>Built in the open. No ads, no tracking, no dark patterns — just a tool that tells you the truth before it touches your system.</sub>
</div>
