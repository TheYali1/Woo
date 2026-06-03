namespace Woo_.Models;

public sealed class ExportRecord
{
    public long Id { get; set; }
    public string AppName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Framework { get; set; } = "Electron";
    public string OutputPath { get; set; } = string.Empty;
    public string? IconPath { get; set; }
    public string? IconUrl { get; set; }
    public bool AdBlockerEnabled { get; set; }
    public bool SingleExe { get; set; }
    public bool IncludeInstaller { get; set; }
    public bool NewLinkRedirect { get; set; }
    public bool AllowDownloads { get; set; } = true;
    public bool CustomScriptsEnabled { get; set; }
    public string? CustomScriptCode { get; set; }
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 800;
    public int BuildDurationSeconds { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Status { get; set; } = "Success";
    public string? BuildLog { get; set; }
    public bool HasIconUrl => !string.IsNullOrWhiteSpace(IconUrl);
    public bool HasCustomScriptCode => !string.IsNullOrWhiteSpace(CustomScriptCode);
    public string BuildDurationText => TimeSpan.FromSeconds(Math.Max(BuildDurationSeconds, 0)).TotalHours >= 1
        ? TimeSpan.FromSeconds(Math.Max(BuildDurationSeconds, 0)).ToString(@"h\:mm\:ss")
        : TimeSpan.FromSeconds(Math.Max(BuildDurationSeconds, 0)).ToString(@"m\:ss");
}
