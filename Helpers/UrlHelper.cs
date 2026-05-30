using System.Globalization;
using System.Text.RegularExpressions;

namespace Woo_.Helpers;

public static class UrlHelper
{
    public static bool TryNormalize(string input, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter a website URL.";
            return false;
        }

        var value = input.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = $"https://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "Enter a valid http or https URL.";
            return false;
        }

        return true;
    }

    public static string SuggestName(Uri uri)
    {
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;

        var firstLabel = host.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Web App";
        firstLabel = Regex.Replace(firstLabel, @"[\-_]+", " ");
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(firstLabel);
    }
}
