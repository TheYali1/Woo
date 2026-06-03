namespace Woo_.Models;

public sealed class AppSettings
{
    public int SettingsSchemaVersion { get; set; } = 5;

    public string DefaultOutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Woo! Exports");

    public string DefaultFramework { get; set; } = "Electron";
    public string Theme { get; set; } = "System";
    public int DefaultWindowWidth { get; set; } = 1280;
    public int DefaultWindowHeight { get; set; } = 800;
    public bool SingleExeByDefault { get; set; }
    public bool IncludeInstallerByDefault { get; set; }
    public bool IncludeAdBlockerByDefault { get; set; }
    public bool EnableDevToolsByDefault { get; set; }
    public bool AllowResizingByDefault { get; set; } = true;
    public bool StartMaximizedByDefault { get; set; }
    public bool ShowMenuBarByDefault { get; set; }
    public bool NewLinkRedirectByDefault { get; set; } = true;
    public bool PersistCookiesByDefault { get; set; } = true;
    public bool MouseNavigationByDefault { get; set; }
    public bool RestrictToMainUrlByDefault { get; set; }
    public bool DisableCachingByDefault { get; set; }
    public bool SystemTrayByDefault { get; set; }
    public bool CustomScriptsByDefault { get; set; }
    public string DefaultCustomScriptCode { get; set; } = string.Empty;
    public string DefaultUserAgentOverride { get; set; } = string.Empty;
    public string DefaultUserAgentPreset { get; set; } = "Custom";
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool ShowBuildCompleteNotification { get; set; } = true;
    public bool CreateDesktopShortcutAfterBuild { get; set; }
    public bool OpenAppAfterBuild { get; set; }
    public bool OpenFolderAfterBuild { get; set; }
    public string DefaultIconBehavior { get; set; } = "Custom";
    public int FaviconFetchTimeoutSeconds { get; set; } = 5;
    public string? FallbackIconPath { get; set; }
    public string? NodePath { get; set; }
    public string? NpmPath { get; set; }
    public string? CargoPath { get; set; }
    public int LastWindowWidth { get; set; } = 1180;
    public int LastWindowHeight { get; set; } = 780;
    public int LastWindowX { get; set; } = -1;
    public int LastWindowY { get; set; } = -1;
}
