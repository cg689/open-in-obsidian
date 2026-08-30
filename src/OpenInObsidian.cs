using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace OpenInObsidian
{
    /// <summary>
    /// Tiny windowless helper for the "Open in Obsidian" file association.
    ///
    /// Problem it solves:
    ///   Obsidian ignores file paths passed on its command line. Double-clicking a
    ///   .md file only launches Obsidian, which then restores the last workspace
    ///   (the previously viewed file), not the file you clicked.
    ///
    /// How it works:
    ///   Windows passes the clicked file path as %1. We check whether the file
    ///   lives inside one of the user's Obsidian vaults (listed in
    ///   %APPDATA%\obsidian\obsidian.json):
    ///     - Inside a vault  -> URL-encode the path and dispatch the official URI
    ///                          obsidian://open?path=...  which makes Obsidian open
    ///                          and focus exactly that file.
    ///     - Outside a vault -> open it with a fallback editor instead, in order:
    ///                          Typora -> VS Code -> Notepad. (The obsidian://open
    ///                          protocol only works for files inside a vault.)
    ///
    /// Why an exe instead of powershell/wscript:
    ///   Compiled with /target:winexe (GUI subsystem), so it never flashes a
    ///   console window. Script hosts either flash a window (powershell) or get
    ///   blocked by security policies (wscript).
    /// </summary>
    internal static class Program
    {
        private const string FallbackConfigFile = "fallback-editor.txt";

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
                    string uri = "obsidian://open?path=" + Uri.EscapeDataString(path);
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                }
                else
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
                json = File.ReadAllText(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "obsidian", "obsidian.json"));
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
                return result;
            }
            catch (Exception ex)
            {
                LogError("parsing obsidian.json", ex);
                return null;
            }
        }

        /// <summary>
        /// Vault-external files can't be opened via obsidian://open, so hand them
        /// to an ordinary editor. Preference: an optional fallback-editor.txt
        /// placed next to this exe (first line = editor path), then Typora,
        /// then VS Code, then Notepad (always present, guaranteed no loop).
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
