using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Woo_.Models;
using Woo_.Services;

namespace Woo_.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private string _defaultOutputDirectory;
    private int _defaultFrameworkIndex;
    private int _themeIndex;
    private double _defaultWindowWidth;
    private double _defaultWindowHeight;
    private bool _singleExeByDefault;
    private bool _includeInstallerByDefault;
    private bool _includeAdBlockerByDefault;
    private bool _enableDevToolsByDefault;
    private bool _allowResizingByDefault;
    private bool _startMaximizedByDefault;
    private bool _showMenuBarByDefault;
    private bool _newLinkRedirectByDefault;
    private bool _persistCookiesByDefault;
    private bool _mouseNavigationByDefault;
    private bool _restrictToMainUrlByDefault;
    private bool _disableCachingByDefault;
    private bool _systemTrayByDefault;
    private bool _customScriptsByDefault;
    private string _defaultCustomScriptCode;
    private string _defaultUserAgentOverride;
    private int _defaultUserAgentPresetIndex;
    private bool _checkUpdatesOnStartup;
    private bool _showBuildCompleteNotification;
    private bool _createDesktopShortcutAfterBuild;
    private bool _openAppAfterBuild;
    private bool _openFolderAfterBuild;
    private int _defaultIconBehaviorIndex;
    private double _faviconFetchTimeoutSeconds;
    private string _fallbackIconPath;
    private string _nodePath;
    private string _npmPath;
    private string _cargoPath;
    private string _updateStatus = string.Empty;
    private string _updateReleaseUrl = string.Empty;
    private int _updateStatusVersion;

    public SettingsViewModel()
    {
        var settings = App.SettingsService.Settings;
        settings.NodePath ??= App.ToolchainService.DetectPath("node.exe");
        settings.NpmPath ??= App.ToolchainService.DetectPath("npm.cmd") ?? App.ToolchainService.DetectPath("npm.exe");
        settings.CargoPath ??= App.ToolchainService.DetectPath("cargo.exe");
        App.SettingsService.Save();

        _defaultOutputDirectory = settings.DefaultOutputDirectory;
        _defaultFrameworkIndex = settings.DefaultFramework.Equals("Tauri", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _themeIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        _defaultWindowWidth = settings.DefaultWindowWidth;
        _defaultWindowHeight = settings.DefaultWindowHeight;
        _singleExeByDefault = settings.SingleExeByDefault;
        _includeInstallerByDefault = settings.IncludeInstallerByDefault && !settings.SingleExeByDefault;
        _includeAdBlockerByDefault = settings.IncludeAdBlockerByDefault;
        _enableDevToolsByDefault = settings.EnableDevToolsByDefault;
        _allowResizingByDefault = settings.AllowResizingByDefault;
        _startMaximizedByDefault = settings.StartMaximizedByDefault;
        _showMenuBarByDefault = settings.ShowMenuBarByDefault;
        _newLinkRedirectByDefault = settings.NewLinkRedirectByDefault;
        _persistCookiesByDefault = settings.PersistCookiesByDefault;
        _mouseNavigationByDefault = settings.MouseNavigationByDefault;
        _restrictToMainUrlByDefault = settings.RestrictToMainUrlByDefault;
        _disableCachingByDefault = settings.DisableCachingByDefault;
        _systemTrayByDefault = settings.SystemTrayByDefault;
        _customScriptsByDefault = settings.CustomScriptsByDefault;
        _defaultCustomScriptCode = settings.DefaultCustomScriptCode;
        _defaultUserAgentOverride = settings.DefaultUserAgentOverride;
        _defaultUserAgentPresetIndex = UserAgentPresets.IndexOf(settings.DefaultUserAgentPreset);
        if (_defaultUserAgentPresetIndex < 0)
        {
            _defaultUserAgentPresetIndex = 0;
        }
        _checkUpdatesOnStartup = settings.CheckUpdatesOnStartup;
        _showBuildCompleteNotification = settings.ShowBuildCompleteNotification;
        _createDesktopShortcutAfterBuild = settings.CreateDesktopShortcutAfterBuild;
        _openAppAfterBuild = settings.OpenAppAfterBuild;
        _openFolderAfterBuild = settings.OpenFolderAfterBuild;
        _defaultIconBehaviorIndex = settings.DefaultIconBehavior == "Custom" ? 1 : 0;
        _faviconFetchTimeoutSeconds = settings.FaviconFetchTimeoutSeconds;
        _fallbackIconPath = settings.FallbackIconPath ?? string.Empty;
        _nodePath = settings.NodePath ?? string.Empty;
        _npmPath = settings.NpmPath ?? string.Empty;
        _cargoPath = settings.CargoPath ?? string.Empty;

        VerifyToolsCommand = new AsyncRelayCommand(VerifyToolsAsync);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
    }

    public ObservableCollection<ToolchainProbeResult> ToolchainResults { get; } = [];
    public ObservableCollection<string> UserAgentPresets { get; } =
    [
        "Custom",
        "Chrome",
        "Edge",
        "Safari",
        "Firefox"
    ];
    public IAsyncRelayCommand VerifyToolsCommand { get; }
    public IAsyncRelayCommand CheckUpdatesCommand { get; }
    public string VersionText => $"Version: {AppUpdateService.CurrentVersion}";

    public string DefaultOutputDirectory
    {
        get => _defaultOutputDirectory;
        set
        {
            if (SetProperty(ref _defaultOutputDirectory, value))
            {
                App.SettingsService.Settings.DefaultOutputDirectory = value;
                Save();
            }
        }
    }

    public int DefaultFrameworkIndex
    {
        get => _defaultFrameworkIndex;
        set
        {
            if (SetProperty(ref _defaultFrameworkIndex, value))
            {
                App.SettingsService.Settings.DefaultFramework = value == 1 ? "Tauri" : "Electron";
                Save();
            }
        }
    }

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (SetProperty(ref _themeIndex, value))
            {
                App.SettingsService.Settings.Theme = value switch { 1 => "Light", 2 => "Dark", _ => "System" };
                Save();
                App.MainWindow?.ApplyTheme();
            }
        }
    }

    public double DefaultWindowWidth
    {
        get => _defaultWindowWidth;
        set
        {
            if (SetProperty(ref _defaultWindowWidth, value))
            {
                App.SettingsService.Settings.DefaultWindowWidth = (int)value;
                Save();
            }
        }
    }

    public double DefaultWindowHeight
    {
        get => _defaultWindowHeight;
        set
        {
            if (SetProperty(ref _defaultWindowHeight, value))
            {
                App.SettingsService.Settings.DefaultWindowHeight = (int)value;
                Save();
            }
        }
    }

    public bool SingleExeByDefault
    {
        get => _singleExeByDefault;
        set
        {
            if (SetProperty(ref _singleExeByDefault, value))
            {
                App.SettingsService.Settings.SingleExeByDefault = value;
                if (value)
                {
                    IncludeInstallerByDefault = false;
                }
                Save();
            }
        }
    }

    public bool IncludeInstallerByDefault
    {
        get => _includeInstallerByDefault;
        set
        {
            if (SetProperty(ref _includeInstallerByDefault, value))
            {
                App.SettingsService.Settings.IncludeInstallerByDefault = value;
                if (value)
                {
                    SingleExeByDefault = false;
                }
                Save();
            }
        }
    }

    public bool IncludeAdBlockerByDefault
    {
        get => _includeAdBlockerByDefault;
        set
        {
            if (SetProperty(ref _includeAdBlockerByDefault, value))
            {
                App.SettingsService.Settings.IncludeAdBlockerByDefault = value;
                Save();
            }
        }
    }

    public bool EnableDevToolsByDefault
    {
        get => _enableDevToolsByDefault;
        set
        {
            if (SetProperty(ref _enableDevToolsByDefault, value))
            {
                App.SettingsService.Settings.EnableDevToolsByDefault = value;
                Save();
            }
        }
    }

    public bool AllowResizingByDefault
    {
        get => _allowResizingByDefault;
        set
        {
            if (SetProperty(ref _allowResizingByDefault, value))
            {
                App.SettingsService.Settings.AllowResizingByDefault = value;
                Save();
            }
        }
    }

    public bool StartMaximizedByDefault
    {
        get => _startMaximizedByDefault;
        set
        {
            if (SetProperty(ref _startMaximizedByDefault, value))
            {
                App.SettingsService.Settings.StartMaximizedByDefault = value;
                Save();
            }
        }
    }

    public bool ShowMenuBarByDefault
    {
        get => _showMenuBarByDefault;
        set
        {
            if (SetProperty(ref _showMenuBarByDefault, value))
            {
                App.SettingsService.Settings.ShowMenuBarByDefault = value;
                Save();
            }
        }
    }

    public bool NewLinkRedirectByDefault
    {
        get => _newLinkRedirectByDefault;
        set
        {
            if (SetProperty(ref _newLinkRedirectByDefault, value))
            {
                App.SettingsService.Settings.NewLinkRedirectByDefault = value;
                Save();
            }
        }
    }

    public bool PersistCookiesByDefault
    {
        get => _persistCookiesByDefault;
        set
        {
            if (SetProperty(ref _persistCookiesByDefault, value))
            {
                App.SettingsService.Settings.PersistCookiesByDefault = value;
                Save();
            }
        }
    }

    public bool MouseNavigationByDefault
    {
        get => _mouseNavigationByDefault;
        set
        {
            if (SetProperty(ref _mouseNavigationByDefault, value))
            {
                App.SettingsService.Settings.MouseNavigationByDefault = value;
                Save();
            }
        }
    }

    public bool RestrictToMainUrlByDefault
    {
        get => _restrictToMainUrlByDefault;
        set
        {
            if (SetProperty(ref _restrictToMainUrlByDefault, value))
            {
                App.SettingsService.Settings.RestrictToMainUrlByDefault = value;
                Save();
            }
        }
    }

    public bool DisableCachingByDefault
    {
        get => _disableCachingByDefault;
        set
        {
            if (SetProperty(ref _disableCachingByDefault, value))
            {
                App.SettingsService.Settings.DisableCachingByDefault = value;
                Save();
            }
        }
    }

    public bool SystemTrayByDefault
    {
        get => _systemTrayByDefault;
        set
        {
            if (SetProperty(ref _systemTrayByDefault, value))
            {
                App.SettingsService.Settings.SystemTrayByDefault = value;
                Save();
            }
        }
    }

    public bool CustomScriptsByDefault
    {
        get => _customScriptsByDefault;
        set
        {
            if (SetProperty(ref _customScriptsByDefault, value))
            {
                App.SettingsService.Settings.CustomScriptsByDefault = value;
                Save();
            }
        }
    }

    public string DefaultCustomScriptCode
    {
        get => _defaultCustomScriptCode;
        set
        {
            if (SetProperty(ref _defaultCustomScriptCode, value))
            {
                App.SettingsService.Settings.DefaultCustomScriptCode = value;
                Save();
            }
        }
    }

    public string DefaultUserAgentOverride
    {
        get => _defaultUserAgentOverride;
        set
        {
            if (SetProperty(ref _defaultUserAgentOverride, value))
            {
                App.SettingsService.Settings.DefaultUserAgentOverride = value;
                Save();
            }
        }
    }

    public int DefaultUserAgentPresetIndex
    {
        get => _defaultUserAgentPresetIndex;
        set
        {
            if (SetProperty(ref _defaultUserAgentPresetIndex, value))
            {
                var preset = value >= 0 && value < UserAgentPresets.Count ? UserAgentPresets[value] : "Custom";
                App.SettingsService.Settings.DefaultUserAgentPreset = preset;
                if (preset != "Custom")
                {
                    DefaultUserAgentOverride = GetUserAgentForPreset(preset);
                }

                Save();
            }
        }
    }

    public bool CheckUpdatesOnStartup
    {
        get => _checkUpdatesOnStartup;
        set
        {
            if (SetProperty(ref _checkUpdatesOnStartup, value))
            {
                App.SettingsService.Settings.CheckUpdatesOnStartup = value;
                Save();
            }
        }
    }

    public bool ShowBuildCompleteNotification
    {
        get => _showBuildCompleteNotification;
        set
        {
            if (SetProperty(ref _showBuildCompleteNotification, value))
            {
                App.SettingsService.Settings.ShowBuildCompleteNotification = value;
                Save();
            }
        }
    }

    public bool CreateDesktopShortcutAfterBuild
    {
        get => _createDesktopShortcutAfterBuild;
        set
        {
            if (SetProperty(ref _createDesktopShortcutAfterBuild, value))
            {
                App.SettingsService.Settings.CreateDesktopShortcutAfterBuild = value;
                Save();
            }
        }
    }

    public bool OpenAppAfterBuild
    {
        get => _openAppAfterBuild;
        set
        {
            if (SetProperty(ref _openAppAfterBuild, value))
            {
                App.SettingsService.Settings.OpenAppAfterBuild = value;
                Save();
            }
        }
    }

    public bool OpenFolderAfterBuild
    {
        get => _openFolderAfterBuild;
        set
        {
            if (SetProperty(ref _openFolderAfterBuild, value))
            {
                App.SettingsService.Settings.OpenFolderAfterBuild = value;
                Save();
            }
        }
    }

    public int DefaultIconBehaviorIndex
    {
        get => _defaultIconBehaviorIndex;
        set
        {
            if (SetProperty(ref _defaultIconBehaviorIndex, value))
            {
                App.SettingsService.Settings.DefaultIconBehavior = value == 1 ? "Custom" : "Auto";
                Save();
            }
        }
    }

    public double FaviconFetchTimeoutSeconds
    {
        get => _faviconFetchTimeoutSeconds;
        set
        {
            if (SetProperty(ref _faviconFetchTimeoutSeconds, value))
            {
                App.SettingsService.Settings.FaviconFetchTimeoutSeconds = Math.Max(1, (int)value);
                Save();
            }
        }
    }

    public string FallbackIconPath
    {
        get => _fallbackIconPath;
        set
        {
            if (SetProperty(ref _fallbackIconPath, value))
            {
                App.SettingsService.Settings.FallbackIconPath = string.IsNullOrWhiteSpace(value) ? null : value;
                Save();
            }
        }
    }

    public string NodePath
    {
        get => _nodePath;
        set
        {
            if (SetProperty(ref _nodePath, value))
            {
                App.SettingsService.Settings.NodePath = string.IsNullOrWhiteSpace(value) ? null : value;
                Save();
            }
        }
    }

    public string NpmPath
    {
        get => _npmPath;
        set
        {
            if (SetProperty(ref _npmPath, value))
            {
                App.SettingsService.Settings.NpmPath = string.IsNullOrWhiteSpace(value) ? null : value;
                Save();
            }
        }
    }

    public string CargoPath
    {
        get => _cargoPath;
        set
        {
            if (SetProperty(ref _cargoPath, value))
            {
                App.SettingsService.Settings.CargoPath = string.IsNullOrWhiteSpace(value) ? null : value;
                Save();
            }
        }
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            if (SetProperty(ref _updateStatus, value))
            {
                OnPropertyChanged(nameof(HasUpdateStatus));
            }
        }
    }

    public bool HasUpdateStatus => !string.IsNullOrWhiteSpace(UpdateStatus);

    public string UpdateReleaseUrl
    {
        get => _updateReleaseUrl;
        private set
        {
            if (SetProperty(ref _updateReleaseUrl, value))
            {
                OnPropertyChanged(nameof(HasUpdateReleaseUrl));
                OnPropertyChanged(nameof(UpdateReleaseUri));
            }
        }
    }

    public Uri? UpdateReleaseUri => string.IsNullOrWhiteSpace(UpdateReleaseUrl)
        ? null
        : new Uri(UpdateReleaseUrl);

    public bool HasUpdateReleaseUrl => !string.IsNullOrWhiteSpace(UpdateReleaseUrl);

    public async Task VerifyToolsAsync()
    {
        ToolchainResults.Clear();
        foreach (var result in await App.ToolchainService.VerifyAsync(App.SettingsService.Settings))
        {
            ToolchainResults.Add(result);
        }
    }

    public async Task CheckUpdatesAsync()
    {
        var version = Interlocked.Increment(ref _updateStatusVersion);
        UpdateStatus = "Checking for updates...";
        UpdateReleaseUrl = string.Empty;
        var result = await App.UpdateService.CheckForUpdatesAsync();
        UpdateStatus = result.Message;
        UpdateReleaseUrl = result.IsUpdateAvailable && !string.IsNullOrWhiteSpace(result.ReleaseUrl)
            ? result.ReleaseUrl
            : string.Empty;

        await Task.Delay(3000);
        if (!result.IsUpdateAvailable && version == _updateStatusVersion)
        {
            UpdateStatus = string.Empty;
            UpdateReleaseUrl = string.Empty;
        }
    }

    public void Reset()
    {
        App.SettingsService.Reset();
    }

    public void UseCurrentUserAgent()
    {
        DefaultUserAgentPresetIndex = 0;
        DefaultUserAgentOverride = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36 Edg/126.0";
    }

    public void ReloadFromSettings()
    {
        var fresh = new SettingsViewModel();
        DefaultOutputDirectory = fresh.DefaultOutputDirectory;
        DefaultFrameworkIndex = fresh.DefaultFrameworkIndex;
        ThemeIndex = fresh.ThemeIndex;
        DefaultWindowWidth = fresh.DefaultWindowWidth;
        DefaultWindowHeight = fresh.DefaultWindowHeight;
        SingleExeByDefault = fresh.SingleExeByDefault;
        IncludeInstallerByDefault = fresh.IncludeInstallerByDefault;
        IncludeAdBlockerByDefault = fresh.IncludeAdBlockerByDefault;
        EnableDevToolsByDefault = fresh.EnableDevToolsByDefault;
        AllowResizingByDefault = fresh.AllowResizingByDefault;
        StartMaximizedByDefault = fresh.StartMaximizedByDefault;
        ShowMenuBarByDefault = fresh.ShowMenuBarByDefault;
        NewLinkRedirectByDefault = fresh.NewLinkRedirectByDefault;
        PersistCookiesByDefault = fresh.PersistCookiesByDefault;
        MouseNavigationByDefault = fresh.MouseNavigationByDefault;
        RestrictToMainUrlByDefault = fresh.RestrictToMainUrlByDefault;
        DisableCachingByDefault = fresh.DisableCachingByDefault;
        SystemTrayByDefault = fresh.SystemTrayByDefault;
        CustomScriptsByDefault = fresh.CustomScriptsByDefault;
        DefaultCustomScriptCode = fresh.DefaultCustomScriptCode;
        DefaultUserAgentPresetIndex = fresh.DefaultUserAgentPresetIndex;
        DefaultUserAgentOverride = fresh.DefaultUserAgentOverride;
        CheckUpdatesOnStartup = fresh.CheckUpdatesOnStartup;
        ShowBuildCompleteNotification = fresh.ShowBuildCompleteNotification;
        CreateDesktopShortcutAfterBuild = fresh.CreateDesktopShortcutAfterBuild;
        OpenAppAfterBuild = fresh.OpenAppAfterBuild;
        OpenFolderAfterBuild = fresh.OpenFolderAfterBuild;
        DefaultIconBehaviorIndex = fresh.DefaultIconBehaviorIndex;
        FaviconFetchTimeoutSeconds = fresh.FaviconFetchTimeoutSeconds;
        FallbackIconPath = fresh.FallbackIconPath;
        NodePath = fresh.NodePath;
        NpmPath = fresh.NpmPath;
        CargoPath = fresh.CargoPath;
    }

    private static string GetUserAgentForPreset(string preset)
    {
        return preset switch
        {
            "Chrome" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36",
            "Edge" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36 Edg/126.0",
            "Safari" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
            "Firefox" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:127.0) Gecko/20100101 Firefox/127.0",
            _ => string.Empty
        };
    }

    private static void Save()
    {
        App.SettingsService.Save();
    }
}
