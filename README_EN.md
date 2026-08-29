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
     │  reads %APPDATA%\obsidian\obsidian.json to check whether the file is in a vault
     │
     ├─ inside a vault → URL-encode the path, dispatch the official protocol
     │     obsidian://open?path=C%3A%5CNotes%5Cmeeting-notes.md
     │     → Obsidian opens and jumps to that file ✅
     │
     └─ outside a vault → open with a fallback editor instead ✅
           (program from fallback-editor.txt → Typora → VS Code → Notepad)
```

### Highlights

- **Zero dependencies**: nothing to download. The installer compiles a small forwarder (~200 lines, vault detection and fallback included) using the .NET Framework compiler that ships with Windows — the source is right there in `src/`, so you can see exactly what gets installed
- **Zero popups**: compiled with `/target:winexe`, a GUI-subsystem program with no console window at all — nothing ever flashes
- **Fallback for vault-external files**: double-clicking a `.md` that lives outside any vault (e.g. a random download) automatically opens it in Typora / VS Code / Notepad instead — because Obsidian's official protocol cannot open files outside a vault (customizable via `fallback-editor.txt`, see FAQ)
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

After installing, double-click a `.md` file inside one of your vaults to try it out. A `.md` outside any vault automatically falls back to Typora / VS Code / Notepad; to pin a specific editor, put its full exe path on a single line in `%LOCALAPPDATA%\OpenInObsidian\fallback-editor.txt`.

> **If double-clicking still opens another app**: a default app you set previously for `.md` (stored in the UserChoice key, which is protected by a Windows ACL) takes precedence. Right-click any `.md` → Open with → Choose another app → pick **Markdown File (Obsidian)** and check "Always use this app" — once is enough.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

This removes the file association, restores your previous `.md` default (automatically backed up at install time), and asks whether to delete the compiled exe.

## Known Limitations

- **Files outside a vault cannot be edited in Obsidian itself.** This is a hard constraint of `obsidian://open?path=`. This project works around it: vault-external `.md` files are automatically opened in a fallback editor (the program from `fallback-editor.txt`, otherwise Typora → VS Code → Notepad). If you want an entire folder inside Obsidian, simply add it as a vault. To edit truly standalone files in Obsidian, see ObsidianShell's VaultRecent mode under Alternatives
- Obsidian won't minimize to the background and pop back — it brings its window to the front and opens the file; that's Obsidian's own behavior
- Windows only (file-association mechanisms on macOS / Linux are completely different)

## Alternatives

This project isn't the first tool to solve this problem — pick whatever fits your needs:

| | This project (open-in-obsidian) | [ObsidianShell](https://github.com/Chaoses-Ib/ObsidianShell) |
|---|---|---|
| Install | One command, compiled on the fly by the system compiler | Download a prebuilt installer |
| Repo contents | Pure source, no binaries | Prebuilt exe |
| Vault-external files | Falls back to Typora / VS Code / Notepad | VaultRecent mode edits them inside Obsidian |
| No popups | ✅ | ✅ |
| Feature scope | Minimal: just "double-click opens *this* file" | Rich: CLI, context menu, launcher workflows, … |

- **[ObsidianShell](https://github.com/Chaoses-Ib/ObsidianShell)**: the feature-complete take on this problem. Its VaultRecent/Recent mode uses directory junctions to temporarily mount standalone files into a "Recent vault", letting Obsidian itself edit vault-external files — more thorough than this project's "fall back to another editor". Great for power users who want Obsidian as a universal Markdown editor
- **Hand-rolled PowerShell / VBS scripts**: nothing to install, but every double-click flashes a console window, and wscript-based ones are often blocked as LOLBins by security policies
- **Just use another editor (Typora / VS Code) as the .md default**: if you don't deeply depend on Obsidian, this is always the simplest solution

## FAQ

**Q: Why not just use a PowerShell script for the forwarding?**
You could, but every double-click would flash a black console window (even `-WindowStyle Hidden` can't avoid the momentary console creation during a shell invocation).

**Q: Why not wscript/VBS?**
`wscript.exe` is flagged as a LOLBin (a binary commonly abused in living-off-the-land attacks) by many security policies and is often blocked outright in corporate environments. A plain compiled exe is much cleaner.

**Q: Could this exe secretly do something else?**
The source is a single file, `src/OpenInObsidian.cs`: read the path → read the vault list to decide where it belongs → dispatch the URI or launch the fallback editor, with an empty catch block. The install script downloads nothing and uses the system's built-in compiler — fully auditable end to end.

**Q: What happens when I double-click a `.md` outside any vault?**
Obsidian's official protocol can't open vault-external files, so they are automatically opened in a fallback editor: the program specified in `%LOCALAPPDATA%\OpenInObsidian\fallback-editor.txt` (one line: full path to the editor exe) if present, otherwise Typora → VS Code, and Notepad as the last resort. Delete the file to go back to auto-detection.

**Q: Obsidian fails to load a vault (EINVAL) and the error mentions `System Volume Information`?**
Don't add an entire drive root (e.g. `E:\`) as a vault. When loading a vault Obsidian scans its root directory and chokes on Windows system-protected folders (hidden + access denied), which fails the whole vault load. Fix: remove the drive-root vault and add specific folders (e.g. `E:\Docs`) instead; loose `.md` files at the drive root will be handled by this project's fallback editor.

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
├── README.md                # Chinese docs
└── README_EN.md             # English docs
```

## License

[MIT](LICENSE) — use it freely; PRs and issues are welcome.
