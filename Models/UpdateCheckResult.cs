namespace Woo_.Models;

public sealed class UpdateCheckResult
{
    public string CurrentVersion { get; init; } = "1.0.1";
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public bool Success { get; init; } = true;
    public string Message { get; init; } = string.Empty;
}
