<div align="center">

<img src="assets/media/clawd-walk.gif" width="170" alt="Clawd the crab walking">

# Clawdows

**Claude Code, at home on Windows. Clawd 🦀 walks while Claude works — and you approve, review and answer right from the tray.**

[![Built for Claude Code](https://img.shields.io/badge/Built%20for-Claude%20Code-D97757?logo=anthropic&logoColor=white)](https://claude.com/claude-code)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows11&logoColor=white)](#-install)
[![Native AOT](https://img.shields.io/badge/.NET-Native%20AOT%20·%20~3MB-512BD4?logo=dotnet&logoColor=white)](#-build-from-source)
[![License: MIT](https://img.shields.io/badge/License-MIT-2ea44f.svg)](LICENSE)

</div>

**Clawdows** (formerly *Claude Status Bar for Windows*) is a tiny native companion for
[Claude Code](https://claude.com/claude-code). Glance at the corner and know what Claude is doing —
**Clawd walks while it works** — and when Claude needs *you* (a permission, a plan, a question),
a small popup appears next to the tray so you can respond **without switching windows**.
One **~3.6 MB native `.exe`** — no runtime, no Node, no Electron.

## ✨ Features

- 🦀 **Clawd, the tray mascot** — walks while Claude is thinking / using tools, idle otherwise.
- 🛂 **Approve permissions from a popup** *(v1.0)* — when Claude asks for permission, a small Claude-Code-style window appears next to the tray with **Allow / Deny / Answer in terminal**. No more switching windows to press `y`.
- 📋 **Plan review** *(v1.0)* — when Claude finishes a plan, a scrollable window renders it (headings, bullets, code) with **Approve / Reject** buttons.
- 🗂 **Multi-session dashboard** *(v1.0)* — the panel lists every active Claude Code session with project, state and timer.
- ↗️ **Terminal jump** *(v1.0)* — click a session to bring its terminal (or IDE — Windows Terminal, VS Code, Cursor, JetBrains…) to the front.
- ❓ **Answer questions from the popup** *(v1.0)* — when Claude asks you something (AskUserQuestion), the options appear as buttons next to the tray — including **multi-select** with checkboxes. Your choice goes back to Claude as feedback and it continues; ignore the popup and the question shows in the terminal as usual.
- 🎯 **Real states** — Editing · Reading · Running a command · Searching · Browsing · Planning · Sub-agent… and recovers correctly on `Esc`.
- 🔔 **Notifications** — silent by default + an optional gentle **chime** only when a *long* task finishes; plus an "are you there?" ping if you walk away.
- 📊 **Usage on hover** — context %, and your 5h / 7d plan usage (no dollar amounts).
- 🎨 **A panel that looks like Claude Code** — dark, rounded, orange accents.
- 🌍 **3 languages** — English · Español · 中文 (auto-detected).
- 🪶 **One self-contained `.exe`** — installs the hooks for you, appears/leaves with your sessions, never starts with Windows.

> **How can it approve permissions?** Claude Code's `PermissionRequest` hook lets an external
> process decide a permission dialog. The popup writes the decision, the hook passes it back —
> no keystroke injection, no window tricks. If the tray isn't running (or you ignore the popup),
> the normal terminal prompt appears as always.

## 👀 What it looks like

<table>
  <tr>
    <td align="center" valign="top">
      <img src="assets/media/panel-main.png" width="230" alt="Main panel"><br>
      <sub>Left-click Clawd → a Claude-Code-style panel</sub>
    </td>
    <td align="center" valign="top">
      <img src="assets/media/panel-notifications.png" width="230" alt="Notifications submenu"><br>
      <sub>Notifications, all configurable</sub>
    </td>
  </tr>
</table>

<img src="assets/media/notification.png" width="360" alt="Windows notification">

<sub>A subtle Windows toast when a long task finishes.</sub>

## 🚀 Install

1. Download **`clawdows.exe`** from the [latest release](../../releases/latest).
2. Put it somewhere permanent, open a terminal there and run:

   ```powershell
   .\clawdows.exe install
   ```

3. Open a **new** Claude Code session. 🦀 Clawd appears and starts following along.

The installer wires Claude Code's hooks into `settings.json` (with a timestamped backup) and, if you
have a custom status line, **preserves it** while adding usage data.

> **Multiple profiles** (e.g. a `claude2` alias with a different `CLAUDE_CONFIG_DIR`)? Install into each:
> ```powershell
> .\clawdows.exe install --config-dir "C:\Users\you\.claude" --config-dir "C:\Users\you\.claude-acc2"
> ```
> **Uninstall:** `.\clawdows.exe uninstall` — removes only its hooks and restores your status line.
> Upgrading from *claude-status-bar*? Just run `.\clawdows.exe install` — it migrates the old hooks automatically.

## 🧩 How it works

Claude Code fires **hooks** at each moment of its work. A tiny, instant invocation of the same `.exe`
writes the current state to a small `state.json`; the tray reads it and draws Clawd. One binary, many hats:

| Command | Role |
|---|---|
| `clawdows` | the tray icon (Clawd) + panel + approval popups |
| `clawdows hook <event>` | writes per-session state (called by the hooks) |
| `clawdows hook permission` | blocks on `PermissionRequest` until you click Allow/Deny (or falls through to the terminal) |
| `clawdows statusline` | feeds usage and passes through your status line |
| `clawdows install` · `uninstall` | wires / unwires everything |

## 🛠️ Build from source

Needs the **.NET 10 SDK** + **Visual C++ build tools** (for the NativeAOT linker).

```powershell
.\build.ps1   # -> .\dist\clawdows.exe
```

CI (GitHub Actions, `windows-latest`) builds and attaches the `.exe` to every tagged release.

## 🙏 Credits

🦀 **Clawd** is from the original macOS project **[m1ckc3s/claude-status-bar](https://github.com/m1ckc3s/claude-status-bar)**
by Mick Cesanek (MIT) — go star it. Windows version & panel by **[@uxKero](https://x.com/uxKero)**.

## 📄 License

[MIT](LICENSE) © uxKero · crab sprite © Mick Cesanek (MIT)

<div align="center">
<sub>Not affiliated with Anthropic. "Claude" and its logo are trademarks of Anthropic.</sub>
</div>
