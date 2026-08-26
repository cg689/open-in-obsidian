# Open in Obsidian

> Double-click any `.md` file, and Obsidian opens **exactly that file** — no popups, no console flash, no reboot required.

[![Windows](https://img.shields.io/badge/platform-Windows-blue)]() [![No dependencies](https://img.shields.io/badge/dependencies-none-green)]() [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

[简体中文](README.md) | **English**

If you use Obsidian on Windows, you've probably run into one of these maddening problems:

**Problem 1: You double-click a `.md` file, but Obsidian opens "the last file you were viewing"**

Obsidian ignores the file path passed on the command line and simply restores your previous workspace on launch. So even if you double-click `meeting-notes.md` in Explorer, what pops up might be yesterday's `shopping-list.md`. This is a [known Obsidian design limitation](https://forum.obsidian.md/t/open-file-from-explorer-opens-last-opened-file/) — nothing is wrong with your system.

**Problem 2: Fixing it with a script makes a black console window flash on every double-click**

A PowerShell wrapper solves Problem 1, but PowerShell is a console program — even `-WindowStyle Hidden` can't prevent that brief black flash when invoked by a registry shell command. Switching to wscript? It gets blocked by security policies as a LOLBin (living-off-the-land binary).

## How This Project Solves It

It combines Obsidian's official URI protocol `obsidian://open?path=...` (which opens a specific file precisely) with a **windowless GUI program compiled on-the-fly in C#** that does the forwarding:

```
Double-click a .md file
     │
     ▼
Windows file association (Obsidian.md ProgId)
     │
     ▼
OpenInObsidian.exe   ← GUI-subsystem program: no console window by design, zero flicker
     │  URL-encode the file path
     ▼
obsidian://open?path=C%3A%5CNotes%5Cmeeting-notes.md
     │
     ▼
Obsidian opens and jumps to that file ✅
```

### Highlights

- **Zero dependencies**: nothing to download. The installer compiles a ~50-line forwarder using the .NET Framework compiler that ships with Windows — the source is right there in `src/`, so you can see exactly what gets installed
- **Zero popups**: compiled with `/target:winexe`, a GUI-subsystem program with no console window at all — nothing ever flashes
- **Instant effect**: the installer calls `SHChangeNotify` to notify Explorer — **no reboot / logoff needed**
- **Per-user registry only (HKCU)**: no admin rights required; the uninstall script restores everything in one command

## Install

Prerequisites: Windows 10/11 + Obsidian installed (any method, including portable/zipped builds) + .NET Framework (bundled with Windows).

Open PowerShell in the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

The script automatically:

1. Locates Obsidian.exe (first from the `obsidian://` protocol handler Obsidian registered itself, then common install directories; you can also pass `-ObsidianPath "D:\path\to\Obsidian.exe"` manually)
2. Compiles `src\OpenInObsidian.cs` → `%LOCALAPPDATA%\OpenInObsidian\OpenInObsidian.exe`
3. Registers the file association and sets it as the default for `.md`
4. Notifies Explorer so it takes effect immediately

After installing, double-click a `.md` file inside one of your vaults to try it out.

> **If double-clicking still opens another app**: a default app you set previously for `.md` (stored in the UserChoice key, which is protected by a Windows ACL) takes precedence. Right-click any `.md` → Open with → Choose another app → pick **Markdown File (Obsidian)** and check "Always use this app" — once is enough.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

This removes the file association, restores your previous `.md` default (automatically backed up at install time), and asks whether to delete the compiled exe.

## Known Limitations

- **Only files inside a vault can be opened.** `obsidian://open?path=` is a hard constraint of Obsidian's official protocol: the file must belong to an existing vault. Standalone `.md` files (e.g. random downloads) won't open. If you often work with md files outside vaults, pair this with Typora / VS Code
- Obsidian won't minimize to the background and pop back — it brings its window to the front and opens the file; that's Obsidian's own behavior
- Windows only (file-association mechanisms on macOS / Linux are completely different)

## FAQ

**Q: Why not just use a PowerShell script for the forwarding?**
You could, but every double-click would flash a black console window (even `-WindowStyle Hidden` can't avoid the momentary console creation during a shell invocation).

**Q: Why not wscript/VBS?**
`wscript.exe` is flagged as a LOLBin (a binary commonly abused in living-off-the-land attacks) by many security policies and is often blocked outright in corporate environments. A plain compiled exe is much cleaner.

**Q: Could this exe secretly do something else?**
The source is only 50 lines, in `src/OpenInObsidian.cs`: read the path → URL-encode it → dispatch the URI, with an empty catch block. The install script downloads nothing and uses the system's built-in compiler — fully auditable end to end.

**Q: Will the `.md` icon change?**
It uses Obsidian's icon (the registration points `DefaultIcon` at Obsidian.exe).

**Q: Do I need to reboot after installing?**
No. The install script calls `SHChangeNotify` to broadcast the association change, so Explorer picks it up immediately. In rare cases (third-party security software hijacking file associations), one logoff/logon is enough.

## Project Structure

```
open-in-obsidian/
├── src/
│   └── OpenInObsidian.cs    # forwarder source (compiled at install time; no binaries in the repo)
├── scripts/
│   ├── install.ps1          # one-command install
│   └── uninstall.ps1        # one-command uninstall
├── LICENSE
└── README.md
```

## License

[MIT](LICENSE) — use it freely; PRs and issues are welcome.
