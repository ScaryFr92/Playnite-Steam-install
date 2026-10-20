using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace SilentInstall.Settings
{
    public class PluginSettings : ObservableObject, ISettings
    {
        private readonly SilentInstallPlugin _plugin;

        [DontSerialize]
        public List<SteamLibrary> DetectedSteamLibraries { get; private set; } = new List<SteamLibrary>();

        private bool _useCustomPath = false;
        public bool UseCustomPath
        {
            get => _useCustomPath;
            set { _useCustomPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(SteamAppsPath)); }
        }

        private string _selectedLibraryPath = string.Empty;
        public string SelectedLibraryPath
        {
            get => _selectedLibraryPath;
            set { _selectedLibraryPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(SteamAppsPath)); }
        }

        private string _customPath = string.Empty;
        public string CustomPath
        {
            get => _customPath;
            set { _customPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(SteamAppsPath)); }
        }

        /// <summary>
        /// When true, a library picker dialog is shown each time the user clicks Install.
        /// When false, the default library below is used without prompting.
        /// </summary>
        private bool _alwaysAskLibrary = true;
        public bool AlwaysAskLibrary
        {
            get => _alwaysAskLibrary;
            set { _alwaysAskLibrary = value; OnPropertyChanged(); }
        }

        /// <summary>The active steamapps path used as the default — custom override or auto-detected.</summary>
        [DontSerialize]
        public string SteamAppsPath => UseCustomPath && !string.IsNullOrWhiteSpace(_customPath)
            ? _customPath
            : _selectedLibraryPath;

        public PluginSettings() { }

        public PluginSettings(SilentInstallPlugin plugin)
        {
            _plugin = plugin;
            RefreshDetectedLibraries();

            var saved = plugin.LoadPluginSettings<PluginSettings>();
            if (saved != null)
            {
                UseCustomPath       = saved.UseCustomPath;
                SelectedLibraryPath = saved.SelectedLibraryPath;
                CustomPath          = saved.CustomPath;
                AlwaysAskLibrary    = saved.AlwaysAskLibrary;
            }

            if (string.IsNullOrEmpty(SelectedLibraryPath) && DetectedSteamLibraries.Count > 0)
                SelectedLibraryPath = DetectedSteamLibraries[0].SteamAppsPath;
        }

        public void RefreshDetectedLibraries()
        {
            DetectedSteamLibraries = SteamHelper.GetLibraries();
            OnPropertyChanged(nameof(DetectedSteamLibraries));
        }

        public void BeginEdit() { }
        public void CancelEdit() { }
        public void EndEdit() => _plugin?.SavePluginSettings(this);

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(SteamAppsPath))
                errors.Add("Steam steamapps path is required.");
            else if (!Directory.Exists(SteamAppsPath))
                errors.Add($"steamapps folder not found: {SteamAppsPath}");
            return errors.Count == 0;
        }
    }
}
