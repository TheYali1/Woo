using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Woo_.Helpers;

public static partial class StringSanitizer
{
    public static string ForFileName(string value, string fallback = "Woo App")
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(source.Length);

        foreach (var character in source)
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        var cleaned = WhitespaceRegex().Replace(builder.ToString(), " ").Trim(' ', '.', '-');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    public static string ForPackageName(string value)
    {
        var normalized = value.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9\-]+", "-");
        normalized = Regex.Replace(normalized, @"\-+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "woo-app" : normalized;
    }

    public static string ForIdentifierPart(string value)
    {
        var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(ForFileName(value, "App").ToLowerInvariant());
        var cleaned = Regex.Replace(title, @"[^A-Za-z0-9]+", string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "App";
        }

        return char.IsDigit(cleaned[0]) ? $"App{cleaned}" : cleaned;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
