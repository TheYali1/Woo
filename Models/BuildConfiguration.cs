namespace Woo_.Models;

public enum OutputFramework
{
    Electron,
    Tauri
}

public enum OutputConflictChoice
{
    Cancel,
    Overwrite,
    Rename
}

public enum AppSourceKind
{
    Website,
    HtmlFile
}

public sealed class BuildConfiguration
{
    public string WebsiteUrl { get; set; } = string.Empty;
    public AppSourceKind SourceKind { get; set; } = AppSourceKind.Website;
    public string LocalSourcePath { get; set; } = string.Empty;
    public string? PackagedSourceEntryRelativePath { get; set; }
    public string AppName { get; set; } = string.Empty;
    public OutputFramework Framework { get; set; } = OutputFramework.Electron;
    public string OutputDirectory { get; set; } = string.Empty;
    public bool AutoFetchIcon { get; set; } = true;
    public string? CustomIconPath { get; set; }
    public string? ResolvedIconPath { get; set; }
    public bool IncludeAdBlocker { get; set; }
    public bool SingleExecutable { get; set; }
    public bool IncludeInstaller { get; set; }
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 800;
    public bool AllowResizing { get; set; } = true;
    public bool StartMaximized { get; set; }
    public bool ShowMenuBar { get; set; }
    public bool EnableDevTools { get; set; }
    public bool NewLinkRedirect { get; set; }
    public bool PersistCookies { get; set; } = true;
    public bool MouseNavigation { get; set; }
    public bool RestrictToMainUrl { get; set; }
    public bool DisableCaching { get; set; }
    public bool AllowDownloads { get; set; } = true;
    public bool SystemTray { get; set; }
    public bool CustomScriptsEnabled { get; set; }
    public string CustomScriptCode { get; set; } = string.Empty;
    public bool CreateDesktopShortcutAfterBuild { get; set; }
    public bool OpenAppAfterBuild { get; set; }
    public bool OpenFolderAfterBuild { get; set; }
    public bool ShowBuildCompleteNotification { get; set; }
    public string? UserAgentOverride { get; set; }
    public string? IconUrl { get; set; }
}
