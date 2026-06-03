namespace Woo_.Models;

public sealed class WooScriptDiagnostic
{
    public int Line { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsWarning { get; init; }
}
