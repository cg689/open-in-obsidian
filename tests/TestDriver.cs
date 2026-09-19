using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

// Reflection-based unit tests for OpenInObsidian.exe.
//
// Run via tests\run-tests.ps1, which compiles both projects into a temp
// directory and executes this driver there. The driver never launches
// Obsidian or any editor and never touches the real Obsidian config: it
// swaps the ObsidianConfigPath seam to a fixture file inside the temp dir
// and stubs the URI-launcher seam (StartUri), so dispatching is never
// really performed.
//
// The junction tests do create real directory junctions - but only inside the
// temp directory, pointing at other folders inside the temp directory.
internal static class TestDriver
{
    private static int failures;

    private static int Main()
    {
        string work = AppDomain.CurrentDomain.BaseDirectory;
        string configPath = Path.Combine(work, "appdata", "obsidian", "obsidian.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath));

        Assembly asm = Assembly.LoadFrom(Path.Combine(work, "OpenInObsidian.exe"));
        Type t = asm.GetType("OpenInObsidian.Program");
        if (t == null)
        {
            Console.WriteLine("  [FAIL] OpenInObsidian.Program not found in assembly");
            return 1;
        }
        MethodInfo getVaults = RequireMethod(t, "GetVaultPaths");
        MethodInfo getCustom = RequireMethod(t, "GetCustomFallbackEditor");
        MethodInfo logError = RequireMethod(t, "LogError");
        MethodInfo linkNameFor = RequireMethod(t, "LinkNameFor");
        MethodInfo stableHash = RequireMethod(t, "StableHash");
        MethodInfo createJunction = RequireMethod(t, "CreateJunction");
        MethodInfo getJunctionTarget = RequireMethod(t, "GetJunctionTarget");
        MethodInfo removeJunction = RequireMethod(t, "RemoveJunction");
        MethodInfo selectLink = RequireMethod(t, "SelectLink");
        MethodInfo readBridgeLinks = RequireMethod(t, "ReadBridgeLinks");
        MethodInfo overlapsBridge = RequireMethod(t, "OverlapsBridgeVault");
        MethodInfo evictOverCap = RequireMethod(t, "EvictOverCap");
        MethodInfo loadState = RequireMethod(t, "LoadMountState");
        MethodInfo saveState = RequireMethod(t, "SaveMountState");
        MethodInfo dispatch = RequireMethod(t, "Dispatch");
        MethodInfo tryBridge = RequireMethod(t, "TryOpenInBridgeVault");
        FieldInfo startUri = t.GetField("StartUri",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        FieldInfo capField = t.GetField("MaxMountedLinks",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        // Redirect the config-path seam to our fixture (env-var APPDATA changes
        // do NOT work: GetFolderPath resolves CSIDL_APPDATA from the registry).
        FieldInfo seam = t.GetField("ObsidianConfigPath",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Func<string> fixture = delegate { return configPath; };
        seam.SetValue(null, fixture);

        // --- 1. valid config: Chinese path, forward slashes, trailing backslash ---
        WriteObsidianJson("{\"vaults\":{\"a\":{\"path\":\"E:\\\\文档\",\"ts\":1},\"b\":{\"path\":\"E:/github-project\\\\\",\"open\":true}}}");
        var vaults = (List<string>)getVaults.Invoke(null, null);
        Check("valid config -> 2 vaults", vaults != null && vaults.Count == 2);
        Check("Chinese vault path parsed", vaults != null && vaults.Contains("E:\\文档\\"));
        Check("forward slashes + trailing backslash normalized", vaults != null && vaults.Contains("E:\\github-project\\"));

        // --- 2. prefix-overlap boundary: vault "E:\\st" vs files in E:\\study2 ---
        WriteObsidianJson("{\"vaults\":{\"c\":{\"path\":\"E:\\\\st\"}}}");
        vaults = (List<string>)getVaults.Invoke(null, null);
        Check("vault anchored with trailing separator",
            vaults != null && vaults.Count == 1 && vaults[0] == "E:\\st\\");
        Check("E:\\study2\\note.md does NOT match E:\\st\\",
            !@"E:\study2\note.md".StartsWith("E:\\st\\", StringComparison.OrdinalIgnoreCase));
        Check("E:\\st\\a.md DOES match",
            @"E:\st\a.md".StartsWith("E:\\st\\", StringComparison.OrdinalIgnoreCase));

        // --- 2b. nested vaults: most specific (longest) root comes first ---
        WriteObsidianJson("{\"vaults\":{\"root\":{\"path\":\"E:\\\\\"},\"docs\":{\"path\":\"E:\\\\docs\"}}}");
        vaults = (List<string>)getVaults.Invoke(null, null);
        Check("nested vaults sorted longest first",
            vaults != null && vaults.Count == 2 && vaults[0] == "E:\\docs\\" && vaults[1] == "E:\\");
        Check("file in E:\\docs\\ claimed by E:\\docs\\ (not E:\\)",
            vaults != null && vaults.Count > 0
                && @"E:\docs\a.md".StartsWith(vaults[0], StringComparison.OrdinalIgnoreCase));

        // --- 3. malformed JSON -> null (caller keeps dispatching to Obsidian) ---
        WriteObsidianJson("{ this is not json");
        Check("malformed JSON -> null", getVaults.Invoke(null, null) == null);

        // --- 4. no "vaults" key -> empty list ---
        WriteObsidianJson("{\"foo\":1}");
        vaults = (List<string>)getVaults.Invoke(null, null);
        Check("missing vaults key -> empty list", vaults != null && vaults.Count == 0);

        // --- 5. obsidian.json missing entirely -> null ---
        File.Delete(configPath);
        Check("missing obsidian.json -> null", getVaults.Invoke(null, null) == null);

        // --- 6. fallback-editor.txt: absent / empty / valid ---
        string cfg = Path.Combine(work, "fallback-editor.txt");
        File.Delete(cfg);
        Check("no config file -> null", getCustom.Invoke(null, null) == null);
        File.WriteAllText(cfg, "");
        Check("empty config file -> null", getCustom.Invoke(null, null) == null);
        string existing = Path.Combine(work, "OpenInObsidian.exe");
        File.WriteAllText(cfg, existing);
        Check("valid config line -> path returned", (string)getCustom.Invoke(null, null) == existing);
        File.Delete(cfg);

        // --- 7. LogError writes last-error.log, keeping only the last error ---
        logError.Invoke(null, new object[] { "first", new InvalidOperationException("one") });
        logError.Invoke(null, new object[] { "second", new InvalidOperationException("two") });
        string log = Path.Combine(work, "last-error.log");
        string content = File.Exists(log) ? File.ReadAllText(log) : "";
        Check("last-error.log keeps last error only", content.Contains("second") && !content.Contains("first"));

        // --- 8. bridge-vault link naming ---
        string namePlain = (string)linkNameFor.Invoke(null, new object[] { @"E:\文档" });
        Check("link name keeps the folder name", namePlain.StartsWith("文档 (", StringComparison.Ordinal));
        Check("link name ends with a hash suffix", namePlain.EndsWith(")", StringComparison.Ordinal));

        string nameDot = (string)linkNameFor.Invoke(null, new object[] { @"E:\notes\.hidden" });
        Check("leading dot stripped (Obsidian would hide it)",
            nameDot.StartsWith("hidden", StringComparison.Ordinal));

        string nameBad = (string)linkNameFor.Invoke(null, new object[] { @"E:\a<b>c|d" });
        Check("invalid filename characters replaced",
            nameBad.IndexOf('<') < 0 && nameBad.IndexOf('>') < 0 && nameBad.IndexOf('|') < 0);

        Check("same folder on two drives -> different links",
            (string)linkNameFor.Invoke(null, new object[] { @"E:\docs" })
            != (string)linkNameFor.Invoke(null, new object[] { @"D:\docs" }));
        Check("hash is case-insensitive (Windows paths are)",
            (string)stableHash.Invoke(null, new object[] { @"E:\Docs" })
            == (string)stableHash.Invoke(null, new object[] { @"e:\docs" }));

        // --- 9. directory junction round trip: create / read / delete ---
        string linkRoot = Path.Combine(work, "junctions");
        string realDir = Path.Combine(work, "real-folder");
        Directory.CreateDirectory(linkRoot);
        Directory.CreateDirectory(realDir);
        File.WriteAllText(Path.Combine(realDir, "note.md"), "# hello");

        string link = Path.Combine(linkRoot, "real-folder");
        bool junctionCreated = (bool)createJunction.Invoke(null, new object[] { link, realDir });
        Check("junction created", junctionCreated);
        if (!junctionCreated)
        {
            // The helper deliberately fails silently at runtime, so the only
            // diagnosis available is the log it leaves behind.
            string diag = Path.Combine(work, "last-error.log");
            Console.WriteLine("  ---- last-error.log ----");
            Console.WriteLine(File.Exists(diag) ? File.ReadAllText(diag).Trim() : "(not written)");
            Console.WriteLine("  ------------------------");
        }
        Check("file reachable through the junction", File.Exists(Path.Combine(link, "note.md")));
        Check("junction target reads back", (string)getJunctionTarget.Invoke(null, new object[] { link }) == realDir);

        removeJunction.Invoke(null, new object[] { link });
        Check("junction removed", !Directory.Exists(link));
        Check("DATA SAFETY: target folder survived junction removal",
            File.Exists(Path.Combine(realDir, "note.md")));

        Check("a plain folder is not mistaken for a junction",
            (string)getJunctionTarget.Invoke(null, new object[] { realDir }) == null);

        // --- 10. link selection: reuse, then nested-conflict handling ---
        string bridge = Path.Combine(work, "bridge");
        string parentDir = Path.Combine(work, "parent");
        string childDir = Path.Combine(parentDir, "child");
        Directory.CreateDirectory(bridge);
        Directory.CreateDirectory(childDir);
        File.WriteAllText(Path.Combine(childDir, "deep.md"), "# deep");

        var names = new List<string>();
        var targets = new List<string>();

        object[] created = { bridge, childDir, names, targets, false };
        string childLink = (string)selectLink.Invoke(null, created);
        Check("new folder gets a junction", childLink != null && Directory.Exists(childLink));
        Check("newly created link reported as created", created[4] is bool && (bool)created[4]);
        Check("nested folder reachable through its junction",
            childLink != null && File.Exists(Path.Combine(childLink, "deep.md")));

        ReadLinks(readBridgeLinks, bridge, names, targets);
        Check("bridge link list has one entry", names.Count == 1 && targets.Count == 1);
        Check("bridge link target matches the folder", targets.Count == 1 && targets[0] == childDir);

        object[] reused = { bridge, childDir, names, targets, false };
        string reusedLink = (string)selectLink.Invoke(null, reused);
        Check("existing junction reused (not recreated)",
            reusedLink == childLink && reused[4] is bool && !(bool)reused[4]);

        // Mounting the parent makes the child link redundant: Obsidian requires
        // link targets to be mutually disjoint, so the nested one must go.
        object[] widened = { bridge, parentDir, names, targets, false };
        string parentLink = (string)selectLink.Invoke(null, widened);
        Check("parent folder gets a junction", parentLink != null && Directory.Exists(parentLink));
        Check("nested child junction removed", !Directory.Exists(childLink));
        Check("child still reachable through the parent junction",
            parentLink != null && File.Exists(Path.Combine(parentLink, "child", "deep.md")));
        Check("DATA SAFETY: child folder survived the conflict cleanup",
            File.Exists(Path.Combine(childDir, "deep.md")));

        ReadLinks(readBridgeLinks, bridge, names, targets);
        Check("bridge now holds exactly one link", names.Count == 1);
        Check("that link points at the parent folder", targets.Count == 1 && targets[0] == parentDir);

        object[] deeper = { bridge, childDir, names, targets, false };
        string underParent = (string)selectLink.Invoke(null, deeper);
        Check("folder under an existing target reuses it (no new junction)",
            parentLink != null
            && underParent == Path.Combine(parentLink, "child")
            && deeper[4] is bool && !(bool)deeper[4]);

        // --- 11. recursion guard: folders overlapping the bridge vault ---
        // Regression for the bug seen in the wild: the bridge vault lives under
        // C:\Users\...\OpenInObsidian\vault, a file directly in C:\Users got a
        // junction to C:\Users, and Obsidian's indexer then walked the vault
        // inside its own mount on every vault load (minute-long loads, hangs).
        string vaultRootExample = @"C:\Users\Administrator\AppData\Local\OpenInObsidian\vault\";
        Check("folder inside the bridge vault overlaps",
            (bool)overlapsBridge.Invoke(null, new object[]
                { Path.Combine(vaultRootExample.TrimEnd('\\'), "sub"), vaultRootExample }));
        Check("the bridge vault itself overlaps",
            (bool)overlapsBridge.Invoke(null, new object[] { vaultRootExample.TrimEnd('\\'), vaultRootExample }));
        Check("an ancestor of the bridge vault overlaps",
            (bool)overlapsBridge.Invoke(null, new object[] { @"C:\Users", vaultRootExample }));
        Check("an unrelated folder does not overlap",
            !(bool)overlapsBridge.Invoke(null, new object[] { @"E:\docs", vaultRootExample }));

        // --- 12. self-heal: dangling junctions are pruned ---
        string danglingTarget = Path.Combine(work, "dangling-target");
        Directory.CreateDirectory(danglingTarget);
        string danglingLink = Path.Combine(bridge, "dangling");
        bool danglingSetUp = (bool)createJunction.Invoke(null, new object[] { danglingLink, danglingTarget });
        Check("dangling junction set up", danglingSetUp);
        Directory.Delete(danglingTarget);   // target gone -> junction dangles

        ReadLinks(readBridgeLinks, bridge, names, targets);
        Check("dangling link pruned from the bridge", !BridgeHasEntry(bridge, "dangling"));
        Check("healthy links survive the pruning",
            names.Count == 1 && targets.Count == 1 && targets[0] == parentDir);

        // --- 13. self-heal: a link mounting an ancestor of the bridge is pruned ---
        string upLink = Path.Combine(bridge, "up-link");
        Check("recursive junction set up",
            (bool)createJunction.Invoke(null, new object[] { upLink, work }));
        ReadLinks(readBridgeLinks, bridge, names, targets);
        Check("ancestor-mounting link pruned", !BridgeHasEntry(bridge, "up-link"));
        Check("DATA SAFETY: the bridge vault survived the ancestor-link pruning",
            Directory.Exists(bridge) && Directory.Exists(Path.Combine(work, "real-folder")));

        // --- 14. LRU: stalest mounts are evicted beyond the cap ---
        int savedCap = (int)capField.GetValue(null);
        capField.SetValue(null, 2);
        try
        {
            string lruA = Path.Combine(work, "lru-a");
            string lruB = Path.Combine(work, "lru-b");
            string lruC = Path.Combine(work, "lru-c");
            Directory.CreateDirectory(lruA);
            Directory.CreateDirectory(lruB);
            Directory.CreateDirectory(lruC);
            File.WriteAllText(Path.Combine(lruA, "a.md"), "a");
            File.WriteAllText(Path.Combine(lruB, "b.md"), "b");
            File.WriteAllText(Path.Combine(lruC, "c.md"), "c");
            createJunction.Invoke(null, new object[] { Path.Combine(bridge, "lru-a"), lruA });
            createJunction.Invoke(null, new object[] { Path.Combine(bridge, "lru-b"), lruB });
            createJunction.Invoke(null, new object[] { Path.Combine(bridge, "lru-c"), lruC });

            ReadLinks(readBridgeLinks, bridge, names, targets);   // parent, lru-a/b/c
            Check("four mounts exist before eviction", names.Count == 4);

            var mountTimes = new Dictionary<string, long>();
            // The parent junction's real name carries the hash suffix from
            // LinkNameFor; state keys must match it exactly.
            string parentName = Path.GetFileName(parentLink);
            mountTimes[parentName] = 200;   // second-stalest
            mountTimes["lru-a"] = 300;
            mountTimes["lru-b"] = 100;      // stalest
            mountTimes["lru-c"] = 400;      // freshest, also the keep-name
            evictOverCap.Invoke(null, new object[] { bridge, names, mountTimes, "lru-c" });

            Check("cap lowered to 2 -> evicted down to two links", names.Count == 2);
            Check("stalest link evicted first", !BridgeHasEntry(bridge, "lru-b"));
            Check("second-stalest link evicted next", !BridgeHasEntry(bridge, parentName));
            Check("recent links kept", BridgeHasEntry(bridge, "lru-a") && BridgeHasEntry(bridge, "lru-c"));
            Check("eviction also drops the state-map entries",
                mountTimes.Count == 2 && mountTimes.ContainsKey("lru-a") && mountTimes.ContainsKey("lru-c"));
            Check("DATA SAFETY: evicted mount's real folder survived",
                File.Exists(Path.Combine(parentDir, "child", "deep.md"))
                && File.Exists(Path.Combine(lruB, "b.md")));
        }
        finally
        {
            capField.SetValue(null, savedCap);
        }

        // --- 15. mount state sidecar round trip ---
        var state = new Dictionary<string, long>();
        state["link with spaces (abc123)"] = 637000000000000000L;
        saveState.Invoke(null, new object[] { state });
        var loaded = (Dictionary<string, long>)loadState.Invoke(null, null);
        Check("mount state survives a save/load round trip",
            loaded != null && loaded.Count == 1
            && loaded.ContainsKey("link with spaces (abc123)")
            && loaded["link with spaces (abc123)"] == 637000000000000000L);
        File.Delete(Path.Combine(work, "mounts.txt"));

        // --- 16. dispatch reports failure instead of throwing, so the caller
        // can fall back to an editor instead of leaving the click dead ---
        // The URI-launcher seam is stubbed, so no URI is ever really started.
        startUri.SetValue(null, (Func<string, bool>)delegate { return true; });
        Check("dispatch succeeds when the shell starts the URI",
            (bool)dispatch.Invoke(null, new object[] { @"E:\docs\note.md" }));
        startUri.SetValue(null, (Func<string, bool>)delegate
        {
            throw new System.ComponentModel.Win32Exception(2, "no handler");
        });
        Check("dispatch with a broken handler returns false (not an exception)",
            !(bool)dispatch.Invoke(null, new object[] { @"E:\docs\note.md" }));
        Check("a failed dispatch is logged",
            File.Exists(log) && File.ReadAllText(log).Contains("dispatching obsidian://open"));

        // --- 17. junctions to UNC network paths use the \??\UNC\ spelling ---
        // A fake share keeps this offline: setting a mount point does not
        // validate the target, and GetJunctionTarget reads the link's own
        // reparse data, never the target.
        string uncLink = Path.Combine(linkRoot, "unc-link");
        string uncTarget = @"\\fake-server\fake-share\docs";
        bool uncCreated = (bool)createJunction.Invoke(null, new object[] { uncLink, uncTarget });
        Check("junction to a UNC path created", uncCreated);
        Check("UNC target reads back",
            (string)getJunctionTarget.Invoke(null, new object[] { uncLink }) == uncTarget);
        removeJunction.Invoke(null, new object[] { uncLink });
        Check("UNC junction removed", !Directory.Exists(uncLink));

        // --- 18. bridge mount end-to-end, with the LRU state kept in sync ---
        // Drives TryOpenInBridgeVault for real (dispatch stubbed): mounting a
        // parent folder drops the nested child junction, and neither the stale
        // link list left behind by that cleanup nor dead sidecar entries may
        // skew eviction. Regression for healthy mounts being evicted although
        // the bridge was never actually over the cap.
        startUri.SetValue(null, (Func<string, bool>)delegate { return true; });
        string bridgeVault = Path.Combine(work, "vault");
        string mountParent = Path.Combine(work, "mount-parent");
        string mountChild = Path.Combine(mountParent, "child");
        string mountOther = Path.Combine(work, "mount-other");
        Directory.CreateDirectory(bridgeVault);
        Directory.CreateDirectory(mountChild);
        Directory.CreateDirectory(mountOther);
        createJunction.Invoke(null, new object[] { Path.Combine(bridgeVault, "l-child"), mountChild });
        createJunction.Invoke(null, new object[] { Path.Combine(bridgeVault, "l-other"), mountOther });

        // l-child is fresh (it will be conflict-removed any moment), l-other is
        // old and healthy, "ghost" has no link behind it at all.
        File.WriteAllText(Path.Combine(work, "mounts.txt"),
            "l-child\t999999999999999999\nl-other\t100\nghost\t50");

        int savedCap2 = (int)capField.GetValue(null);
        capField.SetValue(null, 2);
        try
        {
            var bridgeVaults = new List<string>();
            bridgeVaults.Add(bridgeVault.TrimEnd('\\') + "\\");
            object[] mount = { Path.Combine(mountParent, "note.md"), bridgeVaults };
            Check("bridge mount succeeds end-to-end", (bool)tryBridge.Invoke(null, mount));

            ReadLinks(readBridgeLinks, bridgeVault, names, targets);
            Check("nested child junction replaced by the parent mount",
                !BridgeHasEntry(bridgeVault, "l-child"));
            Check("the healthy old mount was NOT evicted (stale names must not inflate the count)",
                BridgeHasEntry(bridgeVault, "l-other"));
            Check("bridge holds exactly the parent and the old mount",
                names.Count == 2 && targets.Contains(mountParent) && targets.Contains(mountOther));

            string parentLinkName = null;
            for (int i = 0; i < names.Count; i++)
            {
                if (targets[i] == mountParent) { parentLinkName = names[i]; }
            }
            var liveState = (Dictionary<string, long>)loadState.Invoke(null, null);
            Check("mount state keeps exactly the live links (dead and ghost entries pruned)",
                liveState != null && liveState.Count == 2
                && liveState.ContainsKey("l-other")
                && parentLinkName != null && liveState.ContainsKey(parentLinkName)
                && !liveState.ContainsKey("l-child") && !liveState.ContainsKey("ghost"));
            Check("the fresh mount's timestamp was recorded",
                parentLinkName != null && liveState.ContainsKey(parentLinkName)
                && liveState[parentLinkName] > 100);
        }
        finally
        {
            capField.SetValue(null, savedCap2);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : "FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Calls ReadBridgeLinks and copies its two out parameters into the caller's
    /// lists, so tests can drive SelectLink the same way production does.
    /// </summary>
    private static void ReadLinks(MethodInfo readBridgeLinks, string bridge,
        List<string> names, List<string> targets)
    {
        object[] args = { bridge, null, null };
        readBridgeLinks.Invoke(null, args);
        names.Clear();
        targets.Clear();
        names.AddRange((List<string>)args[1]);
        targets.AddRange((List<string>)args[2]);
    }

    /// <summary>
    /// True when the bridge vault folder contains an entry with the given name
    /// (a dangling junction still lists here even though Directory.Exists on
    /// its path returns false).
    /// </summary>
    private static bool BridgeHasEntry(string bridge, string entryName)
    {
        foreach (string sub in Directory.GetDirectories(bridge))
        {
            if (string.Equals(Path.GetFileName(sub), entryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void WriteObsidianJson(string json)
    {
        // Same location the fixture seam (set in Main) points at.
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appdata", "obsidian");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "obsidian.json"), json);
    }

    /// <summary>
    /// Look up a private static method and fail loudly (instead of a later
    /// NullReferenceException) when it was renamed or removed.
    /// </summary>
    private static MethodInfo RequireMethod(Type t, string name)
    {
        MethodInfo m = t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        if (m == null)
        {
            Console.WriteLine("  [FAIL] method '" + name + "' not found in OpenInObsidian.Program" +
                " (renamed or removed? update TestDriver.cs)");
            Environment.Exit(1);
        }
        return m;
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + name);
        if (!ok)
        {
            failures++;
        }
    }
}
