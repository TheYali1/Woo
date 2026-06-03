using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Woo_.Models;

namespace Woo_.Services;

public sealed class AppUpdateService
{
    public const string CurrentVersion = "1.0.1";

    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/TheYali1/Woo/releases/latest");
    private readonly HttpClient _httpClient;

    public AppUpdateService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Woo", CurrentVersion));
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await TryGetLatestReleaseAsync(cancellationToken);

            if (release is null || string.IsNullOrWhiteSpace(release.Version))
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = CurrentVersion,
                    Success = false,
                    Message = "Could not find release information on GitHub."
                };
            }

            var latestVersion = NormalizeVersionText(release.Version);
            var isDifferent = !string.Equals(latestVersion, NormalizeVersionText(CurrentVersion), StringComparison.OrdinalIgnoreCase);
            return new UpdateCheckResult
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = latestVersion,
                ReleaseUrl = release.Url,
                IsUpdateAvailable = isDifferent,
                Message = isDifferent
                    ? $"Woo! {latestVersion} is available on GitHub. You have {CurrentVersion}."
                    : "You're up to date."
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Debug.WriteLine($"Woo update check failed: {ex.Message}");
            return new UpdateCheckResult
            {
                CurrentVersion = CurrentVersion,
                Success = false,
                Message = "Could not check for updates right now."
            };
        }
    }

    private async Task<ReleaseInfo?> TryGetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tagName = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        return new ReleaseInfo(tagName, GetString(root, "html_url") ?? "https://github.com/TheYali1/Woo/releases");
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string NormalizeVersionText(string version)
    {
        return version.Trim().TrimStart('v', 'V');
    }

    private sealed record ReleaseInfo(string Version, string Url);
}
