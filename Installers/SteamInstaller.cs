using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace SilentInstall.Installers
{
    public enum InstallResult { Started, AlreadyQueued, AlreadyInstalled, Failed }

    /// <summary>
    /// Handles silent Steam game installation using the appmanifest trick.
    ///
    /// How it works:
    ///   Steam monitors its steamapps folders at startup. When it finds an
    ///   appmanifest file with StateFlags=1026 ("update started + required"),
    ///   it automatically begins downloading the game — no dialog, no interaction.
    /// </summary>
    public static class SteamInstaller
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        public static readonly Guid SteamPluginId = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        public static bool CanHandle(Game game) => game.PluginId == SteamPluginId;

        /// <summary>
        /// Queues a Steam game for silent installation.
        /// Returns the result so the caller can decide whether to start monitoring.
        /// </summary>
        public static InstallResult Install(Game game, string steamAppsPath, IPlayniteAPI api)
        {
            try
            {
                if (!Directory.Exists(steamAppsPath))
                    throw new InvalidOperationException($"steamapps folder not found: {steamAppsPath}");

                var acfPath = Path.Combine(steamAppsPath, $"appmanifest_{game.GameId}.acf");

                // If an ACF already exists inspect it before overwriting
                if (File.Exists(acfPath))
                {
                    var existing = File.ReadAllText(acfPath);
                    var fm = Regex.Match(existing, "\"StateFlags\"\\s*\"(\\d+)\"");
                    if (fm.Success)
                    {
                        int flags = int.Parse(fm.Groups[1].Value);
                        if ((flags & 4) != 0)
                        {
                            api.Notifications.Add(new NotificationMessage(
                                $"si-steam-already-{game.GameId}",
                                $"\u2705 {game.Name} \u2014 already installed.",
                                NotificationType.Info));
                            return InstallResult.AlreadyInstalled;
                        }
                        // Already in Steam's queue — start monitoring but skip Steam restart
                        api.Notifications.Add(new NotificationMessage(
                            $"si-steam-{game.GameId}",
                            $"\u2b07 {game.Name} \u2014 already queued in Steam. Monitoring\u2026",
                            NotificationType.Info));
                        return InstallResult.AlreadyQueued;
                    }
                }

                EnsureSteamLibraryRegistered(steamAppsPath);

                // Resolve installdir from existing ACFs first (fast, local, reliable).
                // Falls back to sanitized game name — Steam will update it to the real path anyway.
                var installDir = SteamHelper.FindInstallDir(game.GameId)
                    ?? SanitizeDirectoryName(game.Name);

                Log.Info($"[SilentInstall] Installing {game.Name} (appid={game.GameId}) " +
                         $"to {steamAppsPath}, installdir={installDir}");

                WriteAppManifest(acfPath, game.GameId, installDir);
                TriggerOrRestartSteam(acfPath);

                api.Notifications.Add(new NotificationMessage(
                    $"si-steam-{game.GameId}",
                    $"\u2b07 {game.Name} \u2014 queued in Steam. Monitoring until complete\u2026",
                    NotificationType.Info));

                return InstallResult.Started;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[SilentInstall] Install failed for {game.Name}");
                api.Notifications.Add(new NotificationMessage(
                    $"si-steam-err-{game.GameId}",
                    $"Silent Install error: {ex.Message}",
                    NotificationType.Error));
                return InstallResult.Failed;
            }
        }

        // ── Private helpers ───────────────────────────────────────────────

        /// <summary>
        /// Opens steam://open/downloads and waits up to 30 s for Steam to pick up the new ACF.
        /// Only falls back to a full Steam restart if StateFlags stays at 1026 (Steam didn't detect it).
        /// </summary>
        private static void TriggerOrRestartSteam(string acfPath)
        {
            // Signal Steam to check its download queue
            Process.Start(new ProcessStartInfo("steam://open/downloads") { UseShellExecute = true });

            var deadline = DateTime.Now.AddSeconds(30);
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(2000);

                try
                {
                    if (!File.Exists(acfPath)) continue;
                    var content = File.ReadAllText(acfPath);
                    var m = Regex.Match(content, "\"StateFlags\"\\s*\"(\\d+)\"");
                    if (!m.Success) continue;

                    // Steam updated StateFlags away from our initial 1026 → it picked it up without restart
                    if (int.Parse(m.Groups[1].Value) != 1026)
                    {
                        Log.Info("[SilentInstall] Steam picked up ACF without restart.");
                        return;
                    }
                }
                catch { /* keep polling */ }
            }

            // Steam didn't react — do a silent restart
            Log.Info("[SilentInstall] Steam did not detect ACF; performing silent restart.");
            RestartSteamSilently();
        }

        private static void WriteAppManifest(string acfPath, string appId, string installDir)
        {
            File.WriteAllText(acfPath,
                "\"AppState\"\n{\n" +
                $"\t\"appid\"\t\t\"{appId}\"\n" +
                $"\t\"Universe\"\t\"1\"\n" +
                $"\t\"installdir\"\t\"{installDir}\"\n" +
                $"\t\"StateFlags\"\t\"1026\"\n" +
                "}\n");
        }

        /// <summary>
        /// Shuts Steam down completely then restarts it silently.
        /// Uses process polling instead of fixed sleeps so it adapts to machine speed.
        /// </summary>
        private static void RestartSteamSilently()
        {
            var steamExe = SteamHelper.GetExePath();
            if (steamExe == null || !File.Exists(steamExe)) return;

            // Send shutdown signal
            Process.Start(new ProcessStartInfo
            {
                FileName        = steamExe,
                Arguments       = "-shutdown",
                UseShellExecute = false,
                CreateNoWindow  = true
            });

            // Wait for Steam to fully exit (up to 20 seconds)
            var shutdownDeadline = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < shutdownDeadline)
            {
                System.Threading.Thread.Sleep(500);
                if (Process.GetProcessesByName("steam").Length == 0) break;
            }

            // Force-kill any remaining Steam processes
            foreach (var p in Process.GetProcessesByName("steam"))
                try { p.Kill(); } catch { }

            System.Threading.Thread.Sleep(1000);

            // Restart with -silent (system tray only)
            Process.Start(new ProcessStartInfo
            {
                FileName        = steamExe,
                Arguments       = "-silent",
                UseShellExecute = false,
                CreateNoWindow  = true
            });

            // Wait until Steam is back before opening the downloads page
            var launchDeadline = DateTime.Now.AddSeconds(30);
            while (DateTime.Now < launchDeadline)
            {
                System.Threading.Thread.Sleep(1000);
                if (Process.GetProcessesByName("steam").Length > 0) break;
            }

            System.Threading.Thread.Sleep(1500); // brief extra for Steam to accept protocol URLs
            Process.Start(new ProcessStartInfo("steam://open/downloads") { UseShellExecute = true });
        }

        /// <summary>
        /// Ensures the given steamapps folder is registered in Steam's libraryfolders.vdf.
        /// </summary>
        private static void EnsureSteamLibraryRegistered(string steamAppsDir)
        {
            try
            {
                var root = SteamHelper.GetRootPath();
                if (root == null) return;

                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) return;

                var libraryRoot = Path.GetDirectoryName(steamAppsDir);
                if (string.IsNullOrEmpty(libraryRoot)) return;

                var content = File.ReadAllText(vdf);
                var escaped = libraryRoot.Replace("\\", "\\\\");
                if (content.Contains(escaped)) return;

                var maxIdx = 0;
                foreach (Match m in Regex.Matches(content, "\"(\\d+)\""))
                    if (int.TryParse(m.Groups[1].Value, out int idx) && idx > maxIdx)
                        maxIdx = idx;

                var entry    = $"\t\"{maxIdx + 1}\"\n\t{{\n\t\t\"path\"\t\t\"{escaped}\"\n\t}}\n";
                var insertAt = content.LastIndexOf('}');
                if (insertAt > 0)
                    File.WriteAllText(vdf, content.Insert(insertAt, entry));
            }
            catch { /* non-critical */ }
        }

        private static string SanitizeDirectoryName(string name)
            => Regex.Replace(name, @"[<>:""/\\|?*]", string.Empty).Trim();
    }
}
