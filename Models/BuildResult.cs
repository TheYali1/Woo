namespace Woo_.Models;

public sealed class BuildResult
{
    public bool Success { get; init; }
    public bool Canceled { get; init; }
    public string Message { get; init; } = string.Empty;
    public string ProjectDirectory { get; init; } = string.Empty;
    public string LogText { get; init; } = string.Empty;
}
