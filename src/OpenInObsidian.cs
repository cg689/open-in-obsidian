using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
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
            catch
            {
                // Silent by design: this runs on every double-click and must never
                // show a window or error dialog. If Obsidian is missing or the
                // protocol is broken, re-running install.ps1 fixes it.
            }
        }

        /// <summary>
        /// Vault roots from Obsidian's own config, each normalized to end with a
        /// trailing separator so "E:\vault" does not match "E:\vault2\...". Returns
        /// null when the config can't be read.
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
            catch
            {
                return null;
            }

            List<string> vaults = new List<string>();
            foreach (Match m in Regex.Matches(json, @"""path""\s*:\s*""((?:[^""\\]|\\.)*)"""))
            {
                // Normalize separators: some configurations store forward slashes.
                string vault = UnescapeJson(m.Groups[1].Value).Replace('/', '\\').TrimEnd('\\') + "\\";
                if (vault.Length > 1)
                {
                    vaults.Add(vault);
                }
            }
            return vaults;
        }

        private static string UnescapeJson(string s)
        {
            return Regex.Replace(s, @"\\(?:u([0-9a-fA-F]{4})|(.))", m =>
            {
                if (m.Groups[1].Success)
                {
                    return ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString();
                }
                switch (m.Groups[2].Value)
                {
                    case "n": return "\n";
                    case "r": return "\r";
                    case "t": return "\t";
                    default: return m.Groups[2].Value;
                }
            });
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
                    string line = File.ReadAllLines(cfg)[0].Trim();
                    if (line.Length > 0 && File.Exists(line)) { return line; }
                }
            }
            catch { }
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
