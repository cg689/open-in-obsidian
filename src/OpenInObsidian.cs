using System;
using System.Diagnostics;
using System.IO;

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
    ///   Windows passes the clicked file path as %1. We URL-encode it and dispatch
    ///   the official URI  obsidian://open?path=...  which makes Obsidian open and
    ///   focus exactly that file.
    ///
    /// Why an exe instead of powershell/wscript:
    ///   Compiled with /target:winexe (GUI subsystem), so it never flashes a
    ///   console window. Script hosts either flash a window (powershell) or get
    ///   blocked by security policies (wscript).
    /// </summary>
    internal static class Program
    {
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
                string uri = "obsidian://open?path=" + Uri.EscapeDataString(path);
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch
            {
                // Silent by design: this runs on every double-click and must never
                // show a window or error dialog. If Obsidian is missing or the
                // protocol is broken, re-running install.ps1 fixes it.
            }
        }
    }
}
