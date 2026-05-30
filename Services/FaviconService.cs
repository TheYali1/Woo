using System.Net.Http;
using System.Text.RegularExpressions;
using Woo_.Helpers;

namespace Woo_.Services;

public sealed partial class FaviconService
{
    private readonly IconService _iconService;
    private readonly AppSettingsService _settingsService;
    private readonly HttpClient _httpClient = new();

    public FaviconService(IconService iconService, AppSettingsService settingsService)
    {
        _iconService = iconService;
        _settingsService = settingsService;
    }

    public async Task<FaviconMetadata> FetchMetadataAsync(string websiteUrl, CancellationToken cancellationToken = default)
    {
        var title = await FetchTitleAsync(websiteUrl, cancellationToken);
        var icon = await FetchIconAsync(websiteUrl, cancellationToken);

        return new FaviconMetadata
        {
            NormalizedUrl = !string.IsNullOrWhiteSpace(title.NormalizedUrl) ? title.NormalizedUrl : icon.NormalizedUrl,
            SuggestedAppName = title.SuggestedAppName,
            TitleFetchedFromWebsite = title.TitleFetchedFromWebsite,
            IconPath = icon.IconPath,
            IconUrl = icon.IconUrl,
            PreviewPngPath = icon.PreviewPngPath,
            IconFetchedFromWebsite = icon.IconFetchedFromWebsite,
            Error = title.Error ?? icon.Error
        };
    }

    public async Task<FaviconMetadata> FetchTitleAsync(string websiteUrl, CancellationToken cancellationToken = default)
    {
        if (!UrlHelper.TryNormalize(websiteUrl, out var uri, out var error))
        {
            return new FaviconMetadata { Error = error };
        }

        using var timeoutCts = CreateTimeoutToken(cancellationToken);
        var result = new FaviconMetadata
        {
            NormalizedUrl = uri.ToString(),
            SuggestedAppName = UrlHelper.SuggestName(uri)
        };

        try
        {
            using var pageResponse = await _httpClient.GetAsync(uri, timeoutCts.Token);
            if (!pageResponse.IsSuccessStatusCode)
            {
                result.Error = $"Could not fetch title. Server returned {(int)pageResponse.StatusCode}.";
                return result;
            }

            var html = await pageResponse.Content.ReadAsStringAsync(timeoutCts.Token);
            var titleMatch = TitleRegex().Match(html);
            if (!titleMatch.Success)
            {
                result.Error = "Could not find a title tag on this page.";
                return result;
            }

            var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups["title"].Value).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                result.Error = "The page title was empty.";
                return result;
            }

            result.SuggestedAppName = title;
            result.TitleFetchedFromWebsite = true;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result.Error = "Title fetch timed out.";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"Could not fetch title: {ex.Message}";
            return result;
        }
    }

    public async Task<FaviconMetadata> FetchIconAsync(string websiteUrl, CancellationToken cancellationToken = default)
    {
        if (!UrlHelper.TryNormalize(websiteUrl, out var uri, out var error))
        {
            return new FaviconMetadata { Error = error };
        }

        using var timeoutCts = CreateTimeoutToken(cancellationToken);
        var result = new FaviconMetadata
        {
            NormalizedUrl = uri.ToString(),
            IconUrl = $"https://{uri.Host}/favicon.ico"
        };

        try
        {
            var faviconUri = new Uri(result.IconUrl);
            var bytes = await _httpClient.GetByteArrayAsync(faviconUri, timeoutCts.Token);
            var cacheDirectory = Path.Combine(_settingsService.AppDataDirectory, "favicon-cache");
            Directory.CreateDirectory(cacheDirectory);
            var iconPath = Path.Combine(cacheDirectory, $"{uri.Host}.ico");
            var pngPath = Path.Combine(cacheDirectory, $"{uri.Host}.png");
            await _iconService.CreateIcoFromBytesAsync(bytes, iconPath, pngPath);

            result.IconPath = iconPath;
            result.PreviewPngPath = File.Exists(pngPath) ? pngPath : iconPath;
            result.IconFetchedFromWebsite = true;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result.Error = "Favicon fetch timed out.";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"Could not fetch favicon: {ex.Message}";
            return result;
        }
    }

    public FaviconMetadata GetFallbackIcon()
    {
        var fallback = _settingsService.Settings.FallbackIconPath;
        if (string.IsNullOrWhiteSpace(fallback) || !File.Exists(fallback))
        {
            fallback = Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.ico");
        }

        return new FaviconMetadata
        {
            IconPath = File.Exists(fallback) ? fallback : null,
            PreviewPngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.png")
        };
    }

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(_settingsService.Settings.FaviconFetchTimeoutSeconds, 1)));
        return timeoutCts;
    }

    [GeneratedRegex(@"<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();
}

public sealed class FaviconMetadata
{
    public string NormalizedUrl { get; set; } = string.Empty;
    public string SuggestedAppName { get; set; } = string.Empty;
    public bool TitleFetchedFromWebsite { get; set; }
    public string? IconUrl { get; set; }
    public string? IconPath { get; set; }
    public string? PreviewPngPath { get; set; }
    public bool IconFetchedFromWebsite { get; set; }
    public string? Error { get; set; }
}
