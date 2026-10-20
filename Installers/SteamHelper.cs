using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SilentInstall
{
    public class SteamLibrary
    {
        public string SteamAppsPath { get; set; }
        public string Name          { get; set; }
        public long   FreeBytes     { get; set; }
        public long   TotalBytes    { get; set; }

        public double FreeGb  => FreeBytes  / 1_073_741_824.0;
        public double TotalGb => TotalBytes / 1_073_741_824.0;

        public string DisplayLabel =>
            TotalBytes == 0
                ? Name
                : $"{Name}  —  {FreeGb:F0} GB free / {TotalGb:F0} GB";
    }

    /// <summary>Shared Steam utility methods — single source of truth used by Installers and Settings.</summary>
    public static class SteamHelper
    {
        public static string GetRootPath()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                    ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                    return key?.GetValue("InstallPath") as string;
            }
            catch { return null; }
        }

        public static string GetExePath()
        {
            var root = GetRootPath();
            return root != null ? Path.Combine(root, "steam.exe") : null;
        }

        public static List<SteamLibrary> GetLibraries()
        {
            var result = new List<SteamLibrary>();
            try
            {
                var root = GetRootPath();
                if (root == null) return result;

                TryAddLibrary(result, Path.Combine(root, "steamapps"), "Steam (default)");

                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    var content = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        var libPath = m.Groups[1].Value.Replace("\\\\", "\\");
                        var drive   = Path.GetPathRoot(libPath)?.TrimEnd('\\');
                        TryAddLibrary(result, Path.Combine(libPath, "steamapps"), $"Steam Library ({drive})");
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// Looks for an existing ACF in any known Steam library to get the real installdir.
        /// More reliable than the Steam Store API — no network call required.
        /// </summary>
        public static string FindInstallDir(string appId)
        {
            try
            {
                foreach (var lib in GetLibraries())
                {
                    var acf = Path.Combine(lib.SteamAppsPath, $"appmanifest_{appId}.acf");
                    if (!File.Exists(acf)) continue;
                    var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s*\"([^\"]+)\"");
                    if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                        return m.Groups[1].Value;
                }
            }
            catch { }
            return null;
        }

        private static void TryAddLibrary(List<SteamLibrary> list, string steamAppsPath, string fallbackName)
        {
            if (!Directory.Exists(steamAppsPath)) return;
            if (list.Any(l => l.SteamAppsPath.Equals(steamAppsPath, StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                var drive    = new DriveInfo(Path.GetPathRoot(steamAppsPath));
                var volLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? Path.GetPathRoot(steamAppsPath)?.TrimEnd('\\')
                    : drive.VolumeLabel;
                list.Add(new SteamLibrary
                {
                    SteamAppsPath = steamAppsPath,
                    Name          = volLabel ?? fallbackName,
                    FreeBytes     = drive.AvailableFreeSpace,
                    TotalBytes    = drive.TotalSize
                });
            }
            catch
            {
                list.Add(new SteamLibrary { SteamAppsPath = steamAppsPath, Name = fallbackName });
            }
        }
    }
}
