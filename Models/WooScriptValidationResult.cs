namespace Woo_.Models;

public sealed class WooScriptValidationResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<WooScriptDiagnostic> Diagnostics { get; init; } = [];

    public string Summary
    {
        get
        {
            if (!Success && Errors.Count > 0)
            {
                return Errors[0];
            }

            return Warnings.Count > 0
                ? $"Script looks good with {Warnings.Count} warning(s)."
                : "Script looks good.";
        }
    }
}
