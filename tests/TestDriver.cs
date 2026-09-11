using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

// Reflection-based unit tests for OpenInObsidian.exe.
//
// Run via tests\run-tests.ps1, which compiles both projects into a temp
// directory and executes this driver there. The driver never launches
// Obsidian or any editor and never touches the real Obsidian config: it
// swaps the ObsidianConfigPath seam to a fixture file inside the temp dir.
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
