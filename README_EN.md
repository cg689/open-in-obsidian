# Open in Obsidian

> Double-click any `.md` file, and Obsidian opens **exactly that file** — no popups, no console flash, no reboot required.

[![Windows](https://img.shields.io/badge/platform-Windows-blue)]() [![No dependencies](https://img.shields.io/badge/dependencies-none-green)]() [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

[简体中文](README.md) | **English**

If you use Obsidian on Windows, you've probably run into one of these three maddening problems:

**Problem 1: You double-click a `.md` file, but Obsidian opens "the last file you were viewing"**

Obsidian ignores the file path passed on the command line and simply restores your previous workspace on launch. So even if you double-click `meeting-notes.md` in Explorer, what pops up might be yesterday's `shopping-list.md`. This is a [known Obsidian design limitation](https://forum.obsidian.md/t/open-file-from-explorer-opens-last-opened-file/) — nothing is wrong with your system.

**Problem 2: Fixing it with a script makes a black console window flash on every double-click**

A PowerShell wrapper solves Problem 1, but PowerShell is a console program — even `-WindowStyle Hidden` can't prevent that brief black flash when invoked by a registry shell command. Switching to wscript? It gets blocked by security policies as a LOLBin (living-off-the-land binary).

**Problem 3: A `.md` outside every vault can't be opened with Obsidian at all**

`obsidian://open?path=` only searches **registered vaults** for a path that contains it (the official docs say it "will cause the app to search for the most specific vault which contains the specified file path"). If the path is in no vault, the protocol simply does nothing — so either you add every folder as a vault, or you open it with some other editor.

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
     ├─ outside a vault → mount it into the "bridge" vault, then dispatch ✅
     │     a directory junction in the bridge vault points at the file's folder,
     │     so the path becomes a vault-internal one:
     │     obsidian://open?path=…%5Cvault%5CDownloads%20(a1b2c3d4e5f6)%5Ca.md
     │     → Obsidian opens it as a vault file: editable, searchable, linkable
     │
     └─ bridge unavailable (not registered / drive root / path too long)
        → open with a fallback editor instead ✅
          (program from fallback-editor.txt → Typora → VS Code → Notepad)
```

### Highlights

- **Zero dependencies**: nothing to download. The installer compiles a forwarder (~770 lines, vault detection, bridge mounting and fallback included) using the .NET Framework compiler that ships with Windows — the source is right there in `src/`, so you can see exactly what gets installed
- **Zero popups**: compiled with `/target:winexe`, a GUI-subsystem program with no console window at all — nothing ever flashes
- **Vault-external files open in Obsidian too**: the file's folder is mounted into a dedicated "bridge" vault as a directory junction, so `.md` files outside every vault open in Obsidian as well — editable and searchable. The real files never move and none of your own vaults are touched (see below)
- **Falls back when unsure**: when the bridge isn't usable (not registered, file at a drive root, path too long) it opens in Typora / VS Code / Notepad instead — a double-click never just does nothing (customizable via `fallback-editor.txt`, see FAQ)
- **Instant effect**: the installer calls `SHChangeNotify` to notify Explorer — **no reboot / logoff needed**
- **Per-user registry only (HKCU)**: no admin rights required; the uninstall script restores everything in one command

## How the bridge vault works

Obsidian's own documentation supports using symlinks and junctions inside a vault to store files outside it, and it will index and edit the files behind them. That is the mechanism this project uses.

```
C:\Users\<you>\AppData\Local\OpenInObsidian\vault\   ← the bridge vault (created at install time)
├── .obsidian\                    ← an empty vault's config
└── Downloads (a1b2c3d4e5f6)\     ← directory junction pointing at C:\Downloads
    └── some-note.md              ← the path Obsidian sees
```

- **The real files never move**: `C:\Downloads\some-note.md` stays exactly where it is; the junction just opens a door Obsidian recognises
- **Your own vaults are untouched**: the bridge vault stands alone, so no extra folders appear in their file tree, search or graph
- **Junction name = folder name + path hash**: the hash keeps `C:\docs` and `D:\docs` apart; a leading dot is stripped (Obsidian hides dot-folders)
- **No admin rights needed**: unlike symlinks, directory junctions don't require `SeCreateSymbolicLinkPrivilege`
- **Registration is mandatory**: the installer writes the bridge vault into `%APPDATA%\obsidian\obsidian.json` (Obsidian's vault list). **Obsidian must be fully closed** while that happens, otherwise it overwrites the file from its in-memory list — the script detects a running Obsidian and refuses to write

## Install

Prerequisites: Windows 10/11 + Obsidian installed (any method, including portable/zipped builds) + .NET Framework (bundled with Windows).

Open PowerShell in the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

The script automatically:

1. Locates Obsidian.exe (first from the `obsidian://` protocol handler Obsidian registered itself, then common install directories; you can also pass `-ObsidianPath "D:\path\to\Obsidian.exe"` manually)
2. Compiles `src\OpenInObsidian.cs` → `%LOCALAPPDATA%\OpenInObsidian\OpenInObsidian.exe`
3. Creates the bridge vault at `%LOCALAPPDATA%\OpenInObsidian\vault`
4. Registers that bridge vault in Obsidian's vault list (backing up `obsidian.json` first; **Obsidian must be closed**)
5. Registers the file association and sets it as the default for `.md`
6. Notifies Explorer so it takes effect immediately

After installing, double-click any `.md` — inside a vault it opens directly, outside every vault it is mounted into the bridge vault first. To pin an editor for the cases where the bridge isn't usable, put its full exe path on a single line in `%LOCALAPPDATA%\OpenInObsidian\fallback-editor.txt`.

> **If double-clicking still opens another app**: a default app you set previously for `.md` (stored in the UserChoice key, which is protected by a Windows ACL) takes precedence. Right-click any `.md` → Open with → Choose another app → pick **Markdown File (Obsidian)** and check "Always use this app" — once is enough.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1
```

This removes the file association, restores your previous `.md` default (automatically backed up at install time), unregisters the bridge vault from Obsidian's vault list and deletes the bridge vault folder.

> The bridge vault holds nothing but directory junctions, so the uninstall script **removes each junction non-recursively first** and only deletes the folder once nothing is left — a recursive delete would follow the junctions and wipe the real folders they point at.

## Known Limitations

- **The bridge vault must be registered.** If registration failed (say, Obsidian was running during install), vault-external files fall back to the fallback editor instead of doing nothing at all. The installer prints the status, and keeps an `obsidian.json.bak` backup
- **`.md` files at a drive root are not bridged.** Junctioning a whole drive root makes Obsidian scan `System Volume Information` and fail to load the vault (EINVAL), and it would also index the entire system drive. Those files go straight to the fallback editor, and the reason is written to `last-error.log`. Put them in any folder (e.g. `C:\Notes\`) and they work normally
- **Opening a file in a parent folder rebuilds junctions that pointed into its subfolders.** Obsidian requires junction targets to be mutually disjoint, so mounting `E:\a\b` and later `E:\a` replaces the former with the latter. If Obsidian is editing a file in `E:\a\b` at that moment, save it first
- **Obsidian's own wording on symlinks/junctions is "use at your own risk"** — it warns about possible data loss or corruption, and specifically advises against mixing them with sync tools (which all treat links differently). This tool never moves or rewrites your real files, but if you bring bridged content into cloud sync, check your sync tool's behaviour first
- **The first double-click in a new folder takes about 0.4 s longer.** The helper waits for Obsidian's file watcher to notice the freshly created junction, otherwise that first open can miss
- **If Obsidian isn't running, double-clicking a vault-external file makes it record the bridge vault as the last opened one.** A later plain launch of Obsidian may therefore start in the bridge vault instead of your own — and since the bridge vault is empty, it looks like "all my notes are gone". Switch back to your vault, or start Obsidian first and then double-click the file
- Obsidian won't minimize to the background and pop back — it brings its window to the front and opens the file; that's Obsidian's own behavior
- Windows only (file-association mechanisms on macOS / Linux are completely different)

## Alternatives

This project isn't the first tool to solve this problem — pick whatever fits your needs:

| | This project (open-in-obsidian) | [ObsidianShell](https://github.com/Chaoses-Ib/ObsidianShell) |
|---|---|---|
| Install | One command, compiled on the fly by the system compiler | Download a prebuilt installer |
| Repo contents | Pure source, no binaries | Prebuilt exe |
| Vault-external files | Mounted into the bridge vault, edited in Obsidian; falls back when the bridge isn't usable | VaultRecent mode edits them inside Obsidian |
| No popups | ✅ | ✅ |
| Feature scope | Minimal: just "double-click opens *this* file" | Rich: CLI, context menu, launcher workflows, … |

- **[ObsidianShell](https://github.com/Chaoses-Ib/ObsidianShell)**: the feature-complete take on this problem, and the reference for this project's bridging approach. Its VaultRecent/Recent mode also mounts standalone files into a "Recent vault" through directory junctions, and it adds shared vault config, a CLI, a context menu and launcher workflows. Pick it if you want those
- **Hand-rolled PowerShell / VBS scripts**: nothing to install, but every double-click flashes a console window, and wscript-based ones are often blocked as LOLBins by security policies
- **Just use another editor (Typora / VS Code) as the .md default**: if you don't deeply depend on Obsidian, this is always the simplest solution

## FAQ

**Q: Why not just use a PowerShell script for the forwarding?**
You could, but every double-click would flash a black console window (even `-WindowStyle Hidden` can't avoid the momentary console creation during a shell invocation).

**Q: Why not wscript/VBS?**
`wscript.exe` is flagged as a LOLBin (a binary commonly abused in living-off-the-land attacks) by many security policies and is often blocked outright in corporate environments. A plain compiled exe is much cleaner.

**Q: Could this exe secretly do something else?**
The source is a single file, `src/OpenInObsidian.cs`: read the path → read the vault list to decide where it belongs → dispatch the URI if it's inside a vault; otherwise create one directory junction inside the bridge vault and dispatch the URI; only if that fails, launch the fallback editor. The catch block writes a log line and nothing else. The install script downloads nothing and uses the system's built-in compiler — fully auditable end to end.

**Q: What happens when I double-click a `.md` outside any vault?**
The helper creates a directory junction inside the bridge vault pointing at the file's folder (reusing an existing one when possible), then dispatches an `obsidian://open` for the path inside that junction — so Obsidian treats it as a vault file. The real file is never moved or renamed. Only when the bridge isn't usable (bridge vault not registered, file at a drive root, path too long) does it use the fallback editor: the program in `%LOCALAPPDATA%\OpenInObsidian\fallback-editor.txt` (one line: full path to the editor exe) if present, otherwise Typora → VS Code, and Notepad as the last resort.

**Q: Will it delete my files?**
No. The helper only creates and removes **junctions** inside the bridge vault, never real files. Removing a junction uses the non-recursive `RemoveDirectory` — a recursive delete would descend through the junction into the target, so the code deliberately avoids it. The test suite asserts exactly this: after removing a junction the target folder must be intact. Your files are only rewritten when you edit them in Obsidian, as usual.

**Q: Won't the bridge vault accumulate junk?**
It does accumulate — one junction per folder you have opened — but each is a tiny reparse record, not a copy of your files. Re-opening the same folder reuses the junction, and mounting a parent folder automatically cleans up the now-redundant junctions for its subfolders. To clear it all, just delete the bridge vault folder; the next double-click recreates what it needs.

**Q: Will sync tools pick up what's in the bridged folders?**
The real files never move, so a synced folder behaves exactly as before. What you should **not** do is bring `%LOCALAPPDATA%\OpenInObsidian\vault` itself into sync — Obsidian's docs also warn that mixing links with sync tools can cause conflicts.

**Q: Obsidian fails to load a vault (EINVAL) and the error mentions `System Volume Information`?**
Don't add an entire drive root (e.g. `E:\`) as a vault. When loading a vault Obsidian scans its root directory and chokes on Windows system-protected folders (hidden + access denied), which fails the whole vault load. Fix: remove the drive-root vault and add specific folders (e.g. `E:\Docs`) instead. For the same reason this project never bridges `.md` files that sit at a drive root — they go to the fallback editor.

**Q: A double-click does nothing or behaves oddly — how do I debug it?**
The helper never shows anything on screen, but it records the most recent event in `%LOCALAPPDATA%\OpenInObsidian\last-error.log` (single file, last event only). Two kinds of entries show up there:

- internal errors (a failed junction creation, say), with the Win32 error code
- **why the fallback editor was used**, e.g. `fallback | file sits directly in a drive root (C:)`

So a question like "why did this file open in Notepad++?" is answered by that one file. Most issues are fixed by re-running install.ps1.

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
├── tests/
│   ├── run-tests.ps1        # one-command test run (compiles into a temp dir, never touches your real config)
│   └── TestDriver.cs        # 40 unit tests (vault parsing / nested matching / link naming / junction create-read-delete / fallback config / error log)
├── LICENSE
├── README.md                # Chinese docs
└── README_EN.md             # English docs
```

## Running the Tests

Want to verify changes to the source? No install needed:

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

Tests compile the source into a temp directory and drive it via reflection, covering vault parsing (Chinese paths, forward-slash normalization, prefix-overlap boundaries), degraded behavior on malformed / missing configs, fallback-editor.txt reading, and the error log — plus the bridge side: link naming and the stable hash, a create/read/delete round trip for a directory junction, and the nested-folder conflict cleanup.

The junction tests do create real directory junctions, but only inside the temp directory and pointing at folders inside it. Nothing launches Obsidian or touches your real `obsidian.json`. Two assertions guard data safety specifically: after removing a junction the target folder must still be there, and after the conflict cleanup the subfolder's contents must be intact.

## License

[MIT](LICENSE) — use it freely; PRs and issues are welcome.
