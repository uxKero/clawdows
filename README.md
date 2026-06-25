<div align="center">

<img src="assets/media/clawd-walk.gif" width="150" alt="Clawd walking">

# Claude Status Bar — for Windows

**A tiny system-tray indicator for [Claude Code](https://claude.com/claude-code). Clawd 🦀 walks while Claude works.**

[![License: MIT](https://img.shields.io/badge/License-MIT-D97757.svg)](LICENSE)
![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6.svg)
![Native](https://img.shields.io/badge/Native-AOT%20·%20~3MB%20·%20no%20deps-2ea44f.svg)

</div>

---

Glance at the corner of your screen and know — at a glance — what Claude Code is doing,
without staring at the terminal. A little crab named **Clawd** lives in your system tray:
he **walks** while Claude is working, **stops** when it's done, and a subtle toast pings you
when a long task finishes. Hover (or click) to see the current action, an elapsed timer,
and your context / rate-limit usage.

It's a single **~3 MB native `.exe`** — no .NET runtime, no Node, no Python, nothing to install.

<div align="center">

### It tells you when a task finishes — even if you're in another window

<img src="assets/media/notification.png" width="380" alt="Windows notification">

</div>

## ✨ Features

- 🦀 **Clawd, the tray mascot** — walks when Claude is thinking/using tools, stands still when idle.
- 🎯 **Real states, never faked** — reads what Claude Code actually does (Editing, Reading, Running a command, Searching, Browsing, Planning, Sub-agent…). Recovers correctly when you hit `Esc`.
- 🔔 **Configurable notifications** — silent by default, with an optional gentle **chime** only when a *long* task finishes; plus an "are you there?" ping if you walk away.
- 📊 **Usage on hover** — context window %, and your 5h / 7d plan usage (no dollar amounts).
- 🎨 **A panel that looks like Claude Code** — dark, rounded, orange accents (not the gray Windows menu).
- 🌍 **3 languages** — English · Español · 中文 (auto-detects your Windows language).
- 🪶 **Tiny & self-contained** — one native `.exe`, installs your hooks for you, no background bloat.
- 🔌 **Per-session lifecycle** — appears when you start Claude Code, leaves when the last session closes. It does **not** start with Windows.

<div align="center">

<img src="assets/media/panel-main.png" width="240" alt="Main panel">
&nbsp;&nbsp;&nbsp;
<img src="assets/media/panel-notifications.png" width="240" alt="Notifications submenu">

<sub>Left-click Clawd to open the panel · Language · Notifications · Usage · Info</sub>

</div>

## 🚀 Install

1. Download **`claude-status-bar.exe`** from the [latest release](../../releases/latest).
2. Put it somewhere permanent (e.g. `C:\Tools\`), open a terminal there and run:

   ```powershell
   .\claude-status-bar.exe install
   ```

3. Open a **new** Claude Code session. 🦀 Clawd appears in your tray and starts following along.

That's it. The installer wires Claude Code's hooks into your `settings.json` (with a timestamped
backup) and, if you have a custom status line, **preserves it** while adding usage data.

> **Multiple profiles?** If you launch Claude Code several ways (e.g. a `claude2` alias with a
> different `CLAUDE_CONFIG_DIR`), install into each:
> ```powershell
> .\claude-status-bar.exe install --config-dir "C:\Users\you\.claude" --config-dir "C:\Users\you\.claude-acc2"
> ```

### Uninstall

```powershell
.\claude-status-bar.exe uninstall
```

Removes only its own hooks and restores your original status line. Your backups stay in
`<config>\settings.json.bak-statusbar-*`.

## 🧩 How it works

```
Claude Code  ──(hooks)──▶  claude-status-bar.exe hook <event>  ──▶  state.json
                                                                       │
                          claude-status-bar.exe  (tray)  ◀── reads ────┘
                                   │
                                   ▼
                        🦀 Clawd  +  panel  +  toasts
```

Claude Code fires **hooks** at each moment of its work (prompt submitted, about to use a tool,
done, waiting for permission, session start/end). A tiny, instant invocation of the same `.exe`
writes the current state to a small `state.json`. The tray reads it and draws Clawd. The whole
"engine" is just a file — simple to inspect and debug.

The same binary wears several hats via subcommands:

| Command | Role |
|---|---|
| `claude-status-bar` | the tray icon (Clawd) |
| `claude-status-bar hook <event>` | writes `state.json` (called by the hooks) |
| `claude-status-bar statusline` | feeds usage (ctx / 5h / 7d) and passes through your status line |
| `claude-status-bar install` / `uninstall` | wires / unwires everything |

## 🛠️ Build from source

Requires the **.NET 10 SDK** and the **Visual C++ build tools** (for the NativeAOT linker).

```powershell
.\build.ps1   # publishes to .\dist\claude-status-bar.exe
```

The build script makes sure `vswhere.exe` is on `PATH` so NativeAOT can find the linker.
CI (GitHub Actions, `windows-latest`) builds and attaches the `.exe` to each tagged release.

## 🙏 Credits

- 🦀 **Clawd** (the crab sprite) is from the original macOS project
  **[m1ckc3s/claude-status-bar](https://github.com/m1ckc3s/claude-status-bar)** by Mick Cesanek,
  used under the MIT License — see [`assets/crab/ATTRIBUTION.md`](assets/crab/ATTRIBUTION.md).
  Go star the original; it's lovely.
- Built by **[@uxKero](https://x.com/uxKero)**.

## 📄 License

[MIT](LICENSE) © uxKero · crab sprite © Mick Cesanek (MIT)

---

<div align="center">
<sub>Not affiliated with Anthropic. "Claude" and its logo are trademarks of Anthropic.</sub>
</div>
