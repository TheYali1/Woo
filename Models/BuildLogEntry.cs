namespace Woo_.Models;

public sealed class BuildLogEntry
{
    public BuildLogEntry(string text, string severity = "Normal")
    {
        Text = text;
        Severity = severity;
        Timestamp = DateTimeOffset.Now;
    }

    public DateTimeOffset Timestamp { get; }
    public string Text { get; }
    public string Severity { get; }
}
