using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SilentInstall.Installers;
using SilentInstall.Settings;

namespace SilentInstall
{
    public class SilentInstallPlugin : GenericPlugin
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly PluginSettings _settings;

        public override Guid Id { get; } = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        public SilentInstallPlugin(IPlayniteAPI api) : base(api)
        {
            _settings  = new PluginSettings(this);
            Properties = new GenericPluginProperties { HasSettings = true };
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (SteamInstaller.CanHandle(args.Game))
                yield return new SilentSteamInstallController(args.Game, _settings, PlayniteApi);
            else if (EpicInstaller.CanHandle(args.Game))
                yield return new SilentEpicInstallController(args.Game, _settings, PlayniteApi);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (SteamInstaller.CanHandle(args.Game))
                yield return new SilentSteamUninstallController(args.Game, PlayniteApi);
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games.Count != 1) yield break;

            var game    = args.Games[0];
            var isSteam = SteamInstaller.CanHandle(game);
            var isEpic  = EpicInstaller.CanHandle(game);

            if (!isSteam && !isEpic) yield break;

            yield return new GameMenuItem
            {
                Description = isSteam ? "Install silently (Steam)" : "Install silently (Epic)",
                MenuSection  = "Silent Install",
                Action       = _ =>
                {
                    if (isSteam)
                        new SilentSteamInstallController(game, _settings, PlayniteApi)
                            .Install(new InstallActionArgs());
                    else
                        EpicInstaller.Install(game, _settings, PlayniteApi);
                }
            };
        }

        public override ISettings GetSettings(bool firstRunSettings) => _settings;
        public override UserControl GetSettingsView(bool firstRunSettings) => new SettingsView(_settings);
    }

    /// <summary>
    /// Install controller for Steam games.
    /// Shows a library picker, triggers the download, then monitors the appmanifest
    /// until StateFlags indicates the game is fully installed.
    /// </summary>
    public class SilentSteamInstallController : InstallController
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly PluginSettings _settings;
        private readonly IPlayniteAPI   _api;
        private System.Threading.CancellationTokenSource _cts;
        private string _steamAppsPath;

        public SilentSteamInstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            Name      = "Silent Install (Steam)";
            _settings = settings;
            _api      = api;
        }

        public override void Install(InstallActionArgs args)
        {
            _steamAppsPath = PickLibrary();
            if (_steamAppsPath == null) return; // user cancelled the dialog

            var result = SteamInstaller.Install(Game, _steamAppsPath, _api);
            if (result == InstallResult.AlreadyInstalled) return;

            // Monitor whether freshly queued or already in progress
            _cts = new System.Threading.CancellationTokenSource();
            StartMonitoring(_cts.Token);
        }

        /// <summary>
        /// Shows a Steam library picker dialog.
        /// Returns the selected steamapps path, or null if the user cancelled.
        /// </summary>
        private string PickLibrary()
        {
            var libs = SteamHelper.GetLibraries();

            if (libs.Count == 0)
            {
                _api.Dialogs.ShowMessage(
                    "No Steam libraries were detected.\nCheck your Steam installation.",
                    "Silent Install");
                return null;
            }

            // Single library detected — no need to ask
            if (libs.Count == 1)
                return libs[0].SteamAppsPath;

            // "Always ask" is off and a valid default is configured — use it silently
            if (!_settings.AlwaysAskLibrary && !string.IsNullOrEmpty(_settings.SteamAppsPath))
                return _settings.SteamAppsPath;

            // Name = display label shown in bold, Description = path shown as subtitle
            var options = libs
                .Select(l => new GenericItemOption(l.DisplayLabel, l.SteamAppsPath))
                .ToList();

            var selected = _api.Dialogs.ChoiceSelector(options, $"Install \"{Game.Name}\" to\u2026");
            return selected?.Description; // Description holds the steamapps path
        }

        private string InProgressNotifId => $"si-steam-{Game.GameId}";

        private void StartMonitoring(System.Threading.CancellationToken token)
        {
            var acfPath  = System.IO.Path.Combine(_steamAppsPath, $"appmanifest_{Game.GameId}.acf");
            var gameName = Game.Name;
            int lastProgressPct = -1;

            System.Threading.Tasks.Task.Run(() =>
            {
                var deadline    = DateTime.Now.AddHours(24);
                int errorStreak = 0;
                const int maxStreak = 12; // ~1 minute of consecutive errors before warning

                while (!token.IsCancellationRequested && DateTime.Now < deadline)
                {
                    System.Threading.Thread.Sleep(5000);

                    try
                    {
                        if (!System.IO.File.Exists(acfPath)) { errorStreak = 0; continue; }

                        var content   = System.IO.File.ReadAllText(acfPath);
                        var flagMatch = System.Text.RegularExpressions.Regex.Match(
                            content, "\"StateFlags\"\\s*\"(\\d+)\"");

                        if (!flagMatch.Success) { errorStreak = 0; continue; }

                        int flags = int.Parse(flagMatch.Groups[1].Value);

                        // Bit 2 (value 4) = fully installed.
                        // Also mask out in-progress bits to avoid triggering during final staging.
                        const int inProgressBits = 16 | 32 | 256 | 512; // downloading|pre-allocating|staging|committing
                        bool fullyInstalled = (flags & 4) != 0 && (flags & inProgressBits) == 0;

                        if (!fullyInstalled)
                        {
                            errorStreak = 0;
                            UpdateProgressNotification(content, gameName, ref lastProgressPct);
                            continue;
                        }

                        // Fully installed ─────────────────────────────────────────
                        var dirMatch   = System.Text.RegularExpressions.Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
                        var installDir = dirMatch.Success
                            ? System.IO.Path.Combine(_steamAppsPath, "common", dirMatch.Groups[1].Value)
                            : System.IO.Path.Combine(_steamAppsPath, "common", gameName);

                        Log.Info($"[SilentInstall] {gameName} fully installed at {installDir}");

                        _api.Notifications.Remove(InProgressNotifId);
                        _api.Notifications.Add(new NotificationMessage(
                            $"si-steam-done-{Game.GameId}",
                            $"\u2705 {gameName} \u2014 installation complete. Ready to play!",
                            NotificationType.Info));

                        InvokeOnInstalled(new GameInstalledEventArgs
                        {
                            InstalledInfo = new GameInstallationData { InstallDirectory = installDir }
                        });
                        return;
                    }
                    catch (Exception ex)
                    {
                        errorStreak++;
                        Log.Warn(ex, $"[SilentInstall] ACF read error for {gameName} (streak={errorStreak})");
                        if (errorStreak == maxStreak)
                        {
                            _api.Notifications.Add(new NotificationMessage(
                                $"si-steam-warn-{Game.GameId}",
                                $"\u26a0 {gameName} \u2014 trouble reading the appmanifest. Still monitoring\u2026",
                                NotificationType.Error));
                        }
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    _api.Notifications.Remove(InProgressNotifId);
                    _api.Notifications.Add(new NotificationMessage(
                        $"si-steam-timeout-{Game.GameId}",
                        $"\u26a0 {gameName} \u2014 monitoring stopped after 24 h. Check Steam's download queue.",
                        NotificationType.Error));
                }
            }, token);
        }

        private void UpdateProgressNotification(string acfContent, string gameName, ref int lastPct)
        {
            try
            {
                var dlMatch  = System.Text.RegularExpressions.Regex.Match(acfContent, "\"BytesDownloaded\"\\s*\"(\\d+)\"");
                var totMatch = System.Text.RegularExpressions.Regex.Match(acfContent, "\"BytesToDownload\"\\s*\"(\\d+)\"");
                if (!dlMatch.Success || !totMatch.Success) return;

                long downloaded = long.Parse(dlMatch.Groups[1].Value);
                long total      = long.Parse(totMatch.Groups[1].Value);
                if (total <= 0) return;

                int pct = (int)(downloaded * 100L / total);
                if (Math.Abs(pct - lastPct) < 5) return; // update every ~5%

                lastPct = pct;
                double dlGb  = downloaded / 1_073_741_824.0;
                double totGb = total      / 1_073_741_824.0;

                _api.Notifications.Remove(InProgressNotifId);
                _api.Notifications.Add(new NotificationMessage(
                    InProgressNotifId,
                    $"\u2b07 {gameName} \u2014 {dlGb:F1} GB / {totGb:F1} GB ({pct}%)",
                    NotificationType.Info));
            }
            catch { /* non-critical */ }
        }

        public override void Dispose()
        {
            _cts?.Cancel();
            base.Dispose();
        }
    }

    /// <summary>
    /// Uninstall controller for Steam games.
    /// Searches all Steam libraries for the ACF, deletes it and the game folder
    /// so Steam does not automatically re-queue the download on its next startup.
    /// </summary>
    public class SilentSteamUninstallController : UninstallController
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly IPlayniteAPI _api;

        public SilentSteamUninstallController(Game game, IPlayniteAPI api) : base(game)
        {
            Name = "Silent Uninstall (Steam)";
            _api = api;
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            try
            {
                string gameDir = null;

                // Search every Steam library — uninstall works regardless of which lib the game is on
                foreach (var lib in SteamHelper.GetLibraries())
                {
                    var acf = System.IO.Path.Combine(lib.SteamAppsPath, $"appmanifest_{Game.GameId}.acf");
                    if (!System.IO.File.Exists(acf)) continue;

                    var content  = System.IO.File.ReadAllText(acf);
                    var dirMatch = System.Text.RegularExpressions.Regex.Match(
                        content, "\"installdir\"\\s*\"([^\"]+)\"");
                    if (dirMatch.Success && gameDir == null)
                        gameDir = System.IO.Path.Combine(lib.SteamAppsPath, "common", dirMatch.Groups[1].Value);

                    System.IO.File.Delete(acf);
                    Log.Info($"[SilentInstall] Deleted ACF for {Game.Name} in {lib.SteamAppsPath}");
                }

                if (string.IsNullOrEmpty(gameDir) && !string.IsNullOrEmpty(Game.InstallDirectory))
                    gameDir = Game.InstallDirectory;

                if (!string.IsNullOrEmpty(gameDir) && System.IO.Directory.Exists(gameDir))
                {
                    System.IO.Directory.Delete(gameDir, recursive: true);
                    Log.Info($"[SilentInstall] Deleted game folder: {gameDir}");
                }

                InvokeOnUninstalled(new GameUninstalledEventArgs());
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[SilentInstall] Uninstall failed for {Game.Name}");
                _api.Notifications.Add(new NotificationMessage(
                    $"si-steam-uninstall-err-{Game.GameId}",
                    $"Silent Uninstall error: {ex.Message}",
                    NotificationType.Error));
            }
        }
    }

    /// <summary>Install controller for Epic games — opens the Epic Launcher on the game page.</summary>
    public class SilentEpicInstallController : InstallController
    {
        private readonly PluginSettings _settings;
        private readonly IPlayniteAPI   _api;

        public SilentEpicInstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            Name      = "Silent Install (Epic)";
            _settings = settings;
            _api      = api;
        }

        public override void Install(InstallActionArgs args)
            => EpicInstaller.Install(Game, _settings, _api);
    }
}
