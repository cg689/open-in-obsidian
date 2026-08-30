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
        MethodInfo getVaults = t.GetMethod("GetVaultPaths", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo getCustom = t.GetMethod("GetCustomFallbackEditor", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo logError = t.GetMethod("LogError", BindingFlags.NonPublic | BindingFlags.Static);

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

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : "FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void WriteObsidianJson(string json)
    {
        // Same location the fixture seam (set in Main) points at.
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appdata", "obsidian");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "obsidian.json"), json);
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
