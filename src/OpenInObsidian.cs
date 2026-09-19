using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace OpenInObsidian
{
    /// <summary>
    /// Tiny windowless helper for the "Open in Obsidian" file association.
    ///
    /// Problems it solves:
    ///   1. Obsidian ignores file paths passed on its command line, so
    ///      double-clicking a .md only launches Obsidian, which then restores the
    ///      last workspace (the previously viewed file) instead of the file you
    ///      clicked.
    ///   2. Obsidian's obsidian://open?path= only works for files that live inside
    ///      a registered vault, so files elsewhere used to be handed to another
    ///      editor.
    ///
    /// How it works:
    ///   Windows passes the clicked file path as %1. We look it up against the
    ///   user's Obsidian vaults (listed in %APPDATA%\obsidian\obsidian.json):
    ///
    ///     - Inside a vault  -> URL-encode the path and dispatch the official URI
    ///                          obsidian://open?path=...  which makes Obsidian open
    ///                          and focus exactly that file.
    ///
    ///     - Outside a vault -> mount it into the "bridge" vault (see below) and
    ///                          dispatch the same URI with the bridged path. Only if
    ///                          that is not possible do we hand the file to an
    ///                          ordinary editor: fallback-editor.txt -> Typora ->
    ///                          VS Code -> Notepad.
    ///
    /// The bridge vault:
    ///   Obsidian documents that a vault may contain symlinks and junctions that
    ///   point outside the vault, and it will index and edit the files behind them.
    ///   So next to this exe we keep a small vault ("vault\") whose entire content
    ///   is directory junctions pointing at the external folders the user opens
    ///   files from. A junction puts the file behind a path Obsidian considers part
    ///   of a vault, which is what makes the protocol work at all. The real files
    ///   never move, and the user's own vaults are never touched.
    ///
    ///   The vault also stays small no matter how many folders get mounted:
    ///   every run prunes dangling links and links whose target would nest the
    ///   vault inside itself (mounting an ancestor of the vault makes Obsidian's
    ///   indexer walk that recursion on every load - observed in the wild as
    ///   minute-long vault loads), and only the most recently used mounts are
    ///   kept, because Obsidian indexes every mounted folder in full.
    ///
    /// Why an exe instead of powershell/wscript:
    ///   Compiled with /target:winexe (GUI subsystem), so it never flashes a
    ///   console window. Script hosts either flash a window (powershell) or get
    ///   blocked by security policies (wscript).
    /// </summary>
    internal static class Program
    {
        private const string FallbackConfigFile = "fallback-editor.txt";

        /// <summary>Folder next to this exe that acts as the bridge vault.</summary>
        private const string BridgeVaultFolder = "vault";

        /// <summary>Sidecar next to this exe: link name -> last-used ticks.</summary>
        private const string MountStateFile = "mounts.txt";

        /// <summary>
        /// Cap on mounted links. Obsidian indexes every mounted folder in full,
        /// so an unbounded bridge vault would make every vault load slower and
        /// slower. A static field rather than a const so the test driver can
        /// lower it via reflection.
        /// </summary>
        private static int MaxMountedLinks = 10;

        /// <summary>MAX_PATH budget; longer bridged paths would fail to open.</summary>
        private const int MaxPathLength = 259;

        /// <summary>
        /// Seam for unit tests: where to read Obsidian's config from. Tests
        /// replace this to point GetVaultPaths at a fixture file; production
        /// always uses the default (APPDATA\obsidian\obsidian.json).
        /// Private is fine: the test driver reaches it via reflection.
        /// </summary>
        private static Func<string> ObsidianConfigPath = delegate
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "obsidian", "obsidian.json");
        };

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                return;
            }

            string path = args[0].Trim().Trim('"');
            if (path.Length == 0 || !File.Exists(path))
            {
                return;
            }

            try
            {
                // null = obsidian.json unreadable -> we can't tell, keep the old
                // behaviour and just hand the file to Obsidian.
                List<string> vaults = GetVaultPaths();

                bool inVault = vaults == null;
                if (!inVault)
                {
                    foreach (string vault in vaults)
                    {
                        if (path.StartsWith(vault, StringComparison.OrdinalIgnoreCase))
                        {
                            inVault = true;
                            break;
                        }
                    }
                }

                if (inVault)
                {
                    Dispatch(path);
                }
                else if (!TryOpenInBridgeVault(path, vaults))
                {
                    OpenWithFallback(path);
                }
            }
            catch (Exception ex)
            {
                // Silent by design: this runs on every double-click and must never
                // show a window or error dialog. Details go to last-error.log next
                // to this exe so failures can still be diagnosed afterwards.
                LogError("dispatching double-clicked file", ex);
            }
        }

        /// <summary>
        /// Hands the path to Obsidian's official protocol, which opens and focuses
        /// exactly that file (or does nothing, if no registered vault contains it).
        /// </summary>
        private static void Dispatch(string path)
        {
            Process.Start(new ProcessStartInfo("obsidian://open?path=" + Uri.EscapeDataString(path))
            {
                UseShellExecute = true
            });
        }

        /// <summary>
        /// Append-on-overwrite diagnostic log (single file, last error only).
        /// Never throws, never shows anything to the user.
        /// </summary>
        private static void LogError(string context, Exception ex)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-error.log");
                File.WriteAllText(logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + context + " | "
                    + ex.GetType().Name + ": " + ex.Message + Environment.NewLine
                    + ex.StackTrace + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// Records why a vault-external file went to the fallback editor, then tells
        /// the caller to use it. Without this a "why did it open in Notepad++?"
        /// report is undebuggable: the helper is windowless by design, so a bare
        /// "return false" leaves no trace anywhere on the system.
        /// </summary>
        private static bool Fallback(string reason)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-error.log");
                File.WriteAllText(logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | fallback | " + reason
                    + Environment.NewLine);
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Vault roots from Obsidian's own config, each normalized to end with a
        /// trailing separator so "E:\vault" does not match "E:\vault2\...". Returns
        /// null when the config can't be read or parsed (caller then keeps the old
        /// behaviour and hands the file to Obsidian).
        /// </summary>
        private static List<string> GetVaultPaths()
        {
            string json;
            try
            {
                json = File.ReadAllText(ObsidianConfigPath());
            }
            catch (Exception ex)
            {
                // Typical when Obsidian is not installed; logged for diagnosis only.
                LogError("reading obsidian.json", ex);
                return null;
            }

            try
            {
                // Real JSON parsing (JavaScriptSerializer ships with .NET Framework
                // via System.Web.Extensions.dll). Regex-based extraction was
                // dropped: it silently broke whenever Obsidian changed its config
                // layout. Structure: {"vaults": {"<id>": {"path": "...", ...}}}.
                var root = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(json);
                object vaultsObj;
                if (root == null || !root.TryGetValue("vaults", out vaultsObj))
                {
                    return new List<string>();
                }

                var result = new List<string>();
                var vaultMap = vaultsObj as Dictionary<string, object>;
                if (vaultMap == null)
                {
                    return result;
                }
                foreach (var entry in vaultMap.Values)
                {
                    var info = entry as Dictionary<string, object>;
                    if (info == null) { continue; }
                    object pathObj;
                    if (!info.TryGetValue("path", out pathObj)) { continue; }
                    string vaultPath = pathObj as string;
                    if (string.IsNullOrEmpty(vaultPath)) { continue; }

                    // Normalize separators and anchor with a trailing one.
                    string vault = vaultPath.Replace('/', '\\').TrimEnd('\\') + "\\";
                    if (vault.Length > 1)
                    {
                        result.Add(vault);
                    }
                }
                // Longest path first so nested vaults match the most specific
                // root: when both "E:\" and "E:\docs" are vaults, a file inside
                // E:\docs\ must be claimed by E:\docs\ (checked first), not by
                // the shallower E:\. Main() scans the list in order.
                result.Sort((a, b) => b.Length.CompareTo(a.Length));
                return result;
            }
            catch (Exception ex)
            {
                LogError("parsing obsidian.json", ex);
                return null;
            }
        }

        // -----------------------------------------------------------------------
        // Bridge vault: how vault-external files still reach Obsidian
        // -----------------------------------------------------------------------

        /// <summary>
        /// Mounts the file's folder into the bridge vault and dispatches the bridged
        /// path. Returns false - so the caller falls back to an ordinary editor -
        /// whenever anything is off: bridge vault missing or not registered with
        /// Obsidian, folder is a drive root, folder overlaps the bridge vault
        /// itself, path too long, or the mount failed. Never throws.
        /// </summary>
        private static bool TryOpenInBridgeVault(string path, List<string> vaults)
        {
            try
            {
                string bridge = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BridgeVaultFolder);
                string bridgeRoot = bridge.TrimEnd('\\') + "\\";

                // Registration is mandatory: obsidian://open?path= only searches
                // registered vaults, so an unregistered bridge would silently open
                // nothing at all - worse than the fallback editor.
                if (!ContainsVault(vaults, bridgeRoot)) { return Fallback("bridge vault is not registered with Obsidian"); }
                if (!Directory.Exists(bridge)) { return Fallback("bridge vault folder is missing: " + bridge); }

                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) { return Fallback("cannot determine the containing folder"); }
                dir = dir.TrimEnd('\\');
                if (dir.Length == 0 || IsDriveRoot(dir))
                {
                    return Fallback("file sits directly in a drive root (" + dir + ")");
                }

                // Mounting a folder that overlaps the bridge vault - inside it,
                // the vault itself, or an ancestor of it - would nest the vault
                // inside its own mount. Seen in the wild: a file directly in
                // C:\Users mounted the whole profile tree, with the vault under
                // it, and Obsidian's indexer walked that recursion on every
                // vault load (minute-long loads, apparent hangs).
                if (OverlapsBridgeVault(dir, bridgeRoot))
                {
                    return Fallback("folder overlaps the bridge vault: " + dir);
                }

                List<string> names;
                List<string> targets;
                ReadBridgeLinks(bridge, out names, out targets);

                bool mountedNow;
                string linkPath = SelectLink(bridge, dir, names, targets, out mountedNow);
                if (linkPath == null) { return Fallback("could not mount " + dir + " into the bridge vault"); }

                // Bound the vault's size: Obsidian indexes every mounted folder,
                // so accumulating one link per folder forever would make every
                // vault load slower. Track last use and evict the stalest links
                // beyond the cap - never the one about to be used. Deleting a
                // junction only removes the link, never the real folder.
                string linkName = linkPath.Substring(bridgeRoot.Length).Split('\\')[0];
                if (!names.Contains(linkName)) { names.Add(linkName); }
                Dictionary<string, long> mounts = LoadMountState();
                mounts[linkName] = DateTime.UtcNow.Ticks;
                EvictOverCap(bridge, names, mounts, linkName);
                SaveMountState(mounts);

                string bridged = Path.Combine(linkPath, Path.GetFileName(path));
                if (bridged.Length > MaxPathLength)
                {
                    return Fallback("bridged path would be too long (" + bridged.Length + " characters)");
                }

                // A freshly created folder is not in the running Obsidian's index
                // yet, and the protocol resolves paths against that in-memory
                // index - without a grace period the very first double-click in
                // a new folder can look like a no-op. Every other case (Obsidian
                // not running, or another vault active) rescans the disk when it
                // loads the bridge vault, so waiting would only add latency.
                if (mountedNow && IsObsidianRunning()) { Thread.Sleep(1200); }

                Dispatch(bridged);
                return true;
            }
            catch (Exception ex)
            {
                LogError("opening in bridge vault", ex);
                return false;
            }
        }

        private static bool ContainsVault(List<string> vaults, string anchoredPath)
        {
            if (vaults == null) { return false; }
            foreach (string vault in vaults)
            {
                if (string.Equals(vault, anchoredPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when mounting <paramref name="dir"/> would make the bridge vault
        /// contain itself: dir is the bridge, lies inside it, or is one of its
        /// ancestors. Both directions matter - the old guard only rejected
        /// folders inside the vault, so a file directly in C:\Users still
        /// mounted the whole profile tree, with the vault under it.
        /// </summary>
        private static bool OverlapsBridgeVault(string dir, string bridgeRoot)
        {
            string anchored = dir.TrimEnd('\\') + "\\";
            return anchored.StartsWith(bridgeRoot, StringComparison.OrdinalIgnoreCase)
                || bridgeRoot.StartsWith(anchored, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when Obsidian.exe is running. Used to decide whether a freshly
        /// created junction needs a grace period (a running instance learns
        /// about it only through its file watcher; a cold start rescans anyway).
        /// </summary>
        private static bool IsObsidianRunning()
        {
            try
            {
                return Process.GetProcessesByName("Obsidian").Length > 0;
            }
            catch
            {
                return true;   // cannot tell -> keep the graceful behaviour
            }
        }

        /// <summary>
        /// Junctioning a whole drive root would pull System Volume Information into
        /// the vault and make Obsidian fail to load it (EINVAL), so those go to the
        /// fallback editor instead.
        /// </summary>
        private static bool IsDriveRoot(string dir)
        {
            return dir.Length == 2 && dir[1] == ':';
        }

        /// <summary>
        /// Existing junctions in the bridge vault, as parallel name/target lists.
        /// Real folders someone else put there are left alone. Also self-heals:
        /// dangling links (target gone - one of them used to make every later
        /// open crash into the fallback editor) and links whose target overlaps
        /// the bridge vault itself are pruned here. Removing a junction never
        /// touches the folder it pointed at.
        /// </summary>
        private static void ReadBridgeLinks(string bridge, out List<string> names, out List<string> targets)
        {
            names = new List<string>();
            targets = new List<string>();
            string bridgeRoot = bridge.TrimEnd('\\') + "\\";
            foreach (string sub in Directory.GetDirectories(bridge))
            {
                string name = Path.GetFileName(sub);
                if (string.Equals(name, ".obsidian", StringComparison.OrdinalIgnoreCase)) { continue; }

                // GetAttributes follows the junction and throws on a dangling
                // one. Fall through to the reparse point itself: if it carries a
                // target that no longer exists, prune below.
                FileAttributes attributes = 0;
                bool attributesKnown = true;
                try
                {
                    attributes = File.GetAttributes(sub);
                }
                catch
                {
                    attributesKnown = false;
                }
                if (attributesKnown && (attributes & FileAttributes.ReparsePoint) == 0) { continue; }

                string target = GetJunctionTarget(sub);
                if (target == null) { continue; }

                if (!Directory.Exists(target))
                {
                    RemoveJunction(sub);
                    continue;
                }
                if (OverlapsBridgeVault(target, bridgeRoot))
                {
                    RemoveJunction(sub);
                    continue;
                }

                names.Add(name);
                targets.Add(target.TrimEnd('\\'));
            }
        }

        /// <summary>
        /// Picks the bridge-vault path that should hold the file, creating a
        /// junction when needed. An existing link is reused whenever the folder is
        /// the link's target or sits underneath it.
        ///
        /// Before adding a link, any link whose target lies *inside* the new folder
        /// is dropped: Obsidian demands mutually disjoint targets, so the nested
        /// link would be ignored anyway, and the new link already covers its files.
        /// </summary>
        private static string SelectLink(string bridge, string dir, List<string> names,
            List<string> targets, out bool created)
        {
            created = false;

            for (int i = 0; i < names.Count; i++)
            {
                if (dir.Equals(targets[i], StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(bridge, names[i]);
                }
                if (dir.StartsWith(targets[i] + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(bridge, names[i], dir.Substring(targets[i].Length + 1));
                }
            }

            for (int i = 0; i < names.Count; i++)
            {
                if (targets[i].StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    RemoveJunction(Path.Combine(bridge, names[i]));
                }
            }

            string link = Path.Combine(bridge, LinkNameFor(dir));
            if (!CreateJunction(link, dir)) { return null; }

            created = true;
            return link;
        }

        // -----------------------------------------------------------------------
        // Bridge vault size control: last-used tracking + eviction
        // -----------------------------------------------------------------------

        /// <summary>
        /// Last-used times for the bridge links, from the sidecar next to this
        /// exe (one "name<TAB>ticks" line per link). A missing or corrupt file
        /// just means every link starts out "stale"; nothing here may throw.
        /// </summary>
        private static Dictionary<string, long> LoadMountState()
        {
            var mounts = new Dictionary<string, long>();
            try
            {
                string stateFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MountStateFile);
                foreach (string line in File.ReadAllLines(stateFile))
                {
                    int tab = line.IndexOf('\t');
                    long ticks;
                    if (tab > 0 && long.TryParse(line.Substring(tab + 1), out ticks))
                    {
                        mounts[line.Substring(0, tab)] = ticks;
                    }
                }
            }
            catch { }
            return mounts;
        }

        private static void SaveMountState(Dictionary<string, long> mounts)
        {
            try
            {
                string stateFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MountStateFile);
                var lines = new List<string>(mounts.Count);
                foreach (var pair in mounts)
                {
                    lines.Add(pair.Key + "\t" + pair.Value);
                }
                File.WriteAllLines(stateFile, lines);
            }
            catch { }
        }

        /// <summary>
        /// Removes junctions until at most MaxMountedLinks remain, evicting the
        /// least recently used first (unknown last-use counts as oldest). The
        /// link currently being used is never evicted, so opening a file can
        /// never evict its own mount mid-flight.
        /// </summary>
        private static void EvictOverCap(string bridge, List<string> names,
            Dictionary<string, long> mounts, string keepName)
        {
            while (names.Count > MaxMountedLinks)
            {
                int stalest = -1;
                long oldest = long.MaxValue;
                for (int i = 0; i < names.Count; i++)
                {
                    if (string.Equals(names[i], keepName, StringComparison.OrdinalIgnoreCase)) { continue; }
                    long used;
                    if (!mounts.TryGetValue(names[i], out used)) { used = 0; }
                    if (used < oldest)
                    {
                        oldest = used;
                        stalest = i;
                    }
                }
                if (stalest < 0) { break; }

                RemoveJunction(Path.Combine(bridge, names[stalest]));
                mounts.Remove(names[stalest]);
                names.RemoveAt(stalest);
            }
        }

        /// <summary>
        /// Junction name inside the bridge vault: the folder's own name for
        /// readability, plus a hash of the full path so that two folders called
        /// "docs" on different drives cannot collide.
        /// </summary>
        private static string LinkNameFor(string dir)
        {
            // Not Path.GetFileName: on .NET Framework that throws on a path
            // containing invalid characters, and this helper must never throw.
            string name = LastSegment(dir);
            if (string.IsNullOrEmpty(name)) { name = dir; }

            var safe = new StringBuilder();
            foreach (char c in name)
            {
                safe.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            }

            // A leading dot means "hidden" to Obsidian, which would skip the folder.
            name = safe.ToString().Trim().TrimStart('.');
            if (name.Length == 0) { name = "folder"; }
            if (name.Length > 40) { name = name.Substring(0, 40); }

            return name + " (" + StableHash(dir) + ")";
        }

        private static string LastSegment(string path)
        {
            int i = path.LastIndexOfAny(new char[] { '\\', '/' });
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        /// <summary>
        /// FNV-1a over the upper-cased path. Upper-cased because Windows paths are
        /// case-insensitive: E:\Docs and E:\docs must not produce two links.
        /// </summary>
        private static string StableHash(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in text.ToUpperInvariant())
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16").Substring(0, 12);
        }

        // -----------------------------------------------------------------------
        // Directory junction plumbing
        //
        // .NET Framework has no API for junctions (DirectoryInfo.LinkTarget only
        // arrived in .NET 6), so this talks to the reparse-point APIs directly.
        // Junctions rather than symlinks because symlinks need
        // SeCreateSymbolicLinkPrivilege (admin or Developer Mode), while any user
        // can create a junction.
        // -----------------------------------------------------------------------

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ_WRITE = 0x00000003;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

        /// <summary>tag + data length + reserved + four name offset/length pairs.</summary>
        private const int ReparseHeaderSize = 16;

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint controlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern bool RemoveDirectoryW(string pathName);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// Records a failed Win32 call. The junction helpers return false / null on
        /// failure, which is the right behaviour at runtime (the caller falls back),
        /// but it leaves nothing to diagnose afterwards - so the error code goes to
        /// last-error.log like any other failure.
        /// </summary>
        private static void LogWin32(string context)
        {
            LogError(context + " failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        /// <summary>
        /// Creates a junction at <paramref name="linkPath"/> pointing at
        /// <paramref name="targetPath"/>. Returns false (leaving nothing behind)
        /// when the link path is already taken or the reparse point can't be set.
        /// </summary>
        private static bool CreateJunction(string linkPath, string targetPath)
        {
            if (Directory.Exists(linkPath) || File.Exists(linkPath)) { return false; }

            bool cleanupNeeded = false;
            IntPtr handle = IntPtr.Zero;
            try
            {
                Directory.CreateDirectory(linkPath);
                cleanupNeeded = true;

                // The reparse buffer carries two copies of the target: the
                // "substitute" name (\??\C:\dir, what the filesystem resolves) and
                // the "print" name (C:\dir, what tools display). Windows expects
                // both, each null-terminated.
                byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + targetPath);
                byte[] print = Encoding.Unicode.GetBytes(targetPath);
                int pathBufferLength = substitute.Length + 2 + print.Length + 2;

                // The header already carries the four offset/length pairs, so the
                // buffer is header + path buffer; ReparseDataLength counts the
                // MOUNT_POINT_REPARSE_BUFFER (those four pairs + the path buffer).
                int dataLength = 8 + pathBufferLength;
                byte[] buffer = new byte[ReparseHeaderSize + pathBufferLength];
                using (var stream = new MemoryStream(buffer))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(IO_REPARSE_TAG_MOUNT_POINT);
                    writer.Write((ushort)dataLength);
                    writer.Write((ushort)0);                        // Reserved
                    writer.Write((ushort)0);                        // SubstituteNameOffset
                    writer.Write((ushort)substitute.Length);        // SubstituteNameLength
                    writer.Write((ushort)(substitute.Length + 2));  // PrintNameOffset
                    writer.Write((ushort)print.Length);             // PrintNameLength
                    writer.Write(substitute);
                    writer.Write((ushort)0);
                    writer.Write(print);
                    writer.Write((ushort)0);
                }

                handle = CreateFileW(linkPath, GENERIC_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero,
                    OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
                if (handle == InvalidHandle) { LogWin32("opening " + linkPath); return false; }

                IntPtr pinned = Marshal.AllocHGlobal(buffer.Length);
                try
                {
                    Marshal.Copy(buffer, 0, pinned, buffer.Length);
                    uint returned;
                    if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, pinned, (uint)buffer.Length,
                            IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    {
                        LogWin32("setting reparse point on " + linkPath);
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pinned);
                }

                cleanupNeeded = false;
                return true;
            }
            catch (Exception ex)
            {
                LogError("creating junction " + linkPath, ex);
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != InvalidHandle) { CloseHandle(handle); }
                if (cleanupNeeded) { RemoveDirectoryW(linkPath); }
            }
        }

        /// <summary>
        /// Target of a junction, or null when the path is not a mount-point
        /// reparse point (or is one we didn't create - e.g. a volume-GUID link).
        /// </summary>
        private static string GetJunctionTarget(string linkPath)
        {
            const int bufferSize = 16384;
            IntPtr handle = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                handle = CreateFileW(linkPath, GENERIC_READ, FILE_SHARE_READ_WRITE, IntPtr.Zero,
                    OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
                if (handle == InvalidHandle) { LogWin32("opening " + linkPath); return null; }

                buffer = Marshal.AllocHGlobal(bufferSize);
                uint returned;
                if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0,
                        buffer, bufferSize, out returned, IntPtr.Zero))
                {
                    LogWin32("reading reparse point on " + linkPath);
                    return null;
                }

                if ((uint)Marshal.ReadInt32(buffer) != IO_REPARSE_TAG_MOUNT_POINT) { return null; }

                int offset = Marshal.ReadInt16(buffer, 8);
                int length = Marshal.ReadInt16(buffer, 10);
                if (length <= 0) { return null; }

                string substitute = Marshal.PtrToStringUni(
                    new IntPtr(buffer.ToInt64() + ReparseHeaderSize + offset), length / 2);
                if (substitute == null) { return null; }

                // "\??\C:\dir" (and the \\?\ spelling) -> "C:\dir".
                if (substitute.StartsWith(@"\??\", StringComparison.Ordinal)
                    || substitute.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    return substitute.Substring(4);
                }
                return null;
            }
            catch (Exception ex)
            {
                LogError("reading junction " + linkPath, ex);
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); }
                if (handle != IntPtr.Zero && handle != InvalidHandle) { CloseHandle(handle); }
            }
        }

        /// <summary>
        /// Deletes the junction itself. RemoveDirectory never descends into the
        /// target - Directory.Delete(path, recursive: true) WOULD wipe the user's
        /// real folder, so it must never be used on a bridge-vault entry.
        /// </summary>
        private static void RemoveJunction(string linkPath)
        {
            try
            {
                RemoveDirectoryW(linkPath);
            }
            catch (Exception ex)
            {
                LogError("removing junction " + linkPath, ex);
            }
        }

        // -----------------------------------------------------------------------
        // Fallback editor
        // -----------------------------------------------------------------------

        /// <summary>
        /// Last resort for files we cannot get into Obsidian. Preference: an
        /// optional fallback-editor.txt placed next to this exe (first line =
        /// editor path), then Typora, then VS Code, then Notepad (always present,
        /// guaranteed no loop).
        /// </summary>
        private static void OpenWithFallback(string path)
        {
            string custom = GetCustomFallbackEditor();
            if (custom != null)
            {
                StartEditor(custom, path);
                return;
            }

            foreach (string exe in new string[] { FindApp("Typora.exe"), FindApp("Code.exe") })
            {
                if (exe != null)
                {
                    StartEditor(exe, path);
                    return;
                }
            }

            // Last resort: Notepad. Never Process.Start(path) here - the default
            // handler for .md is this exe itself, which would loop forever.
            StartEditor(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), path);
        }

        private static string GetCustomFallbackEditor()
        {
            try
            {
                string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FallbackConfigFile);
                if (File.Exists(cfg))
                {
                    string[] lines = File.ReadAllLines(cfg);
                    if (lines.Length > 0)
                    {
                        string line = lines[0].Trim();
                        if (line.Length > 0 && File.Exists(line)) { return line; }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("reading " + FallbackConfigFile, ex);
            }
            return null;
        }

        /// <summary>App Paths registry lookup (HKCU then HKLM), then common install dirs.</summary>
        private static string FindApp(string exeName)
        {
            foreach (string root in new string[]
            {
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\" + exeName
            })
            {
                try
                {
                    string v = (string)Registry.GetValue(root, null, null);
                    if (!string.IsNullOrEmpty(v))
                    {
                        // App Paths default values are usually the plain exe path,
                        // sometimes quoted, rarely with launcher prefix/args.
                        foreach (string candidate in new string[]
                        {
                            v.Trim('"'),
                            v.Trim('"').Split(' ')[0].Trim('"')
                        })
                        {
                            if (candidate.EndsWith(exeName, StringComparison.OrdinalIgnoreCase)
                                && File.Exists(candidate))
                            {
                                return candidate;
                            }
                        }
                    }
                }
                catch { }
            }

            // Standard install locations (user-scope then machine-scope).
            string localPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            foreach (string p in new string[]
            {
                // VS Code user installer: ...\Programs\Microsoft VS Code\Code.exe
                Path.Combine(localPrograms, "Microsoft VS Code", exeName),
                // Typora machine installer
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    exeName.Replace(".exe", ""), exeName),
                Path.Combine(localPrograms, exeName.Replace(".exe", ""), exeName)
            })
            {
                if (File.Exists(p)) { return p; }
            }
            return null;
        }

        private static void StartEditor(string exe, string path)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "\"" + path + "\"",
                UseShellExecute = true
            });
        }
    }
}
