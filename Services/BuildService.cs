using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Woo_.Helpers;
using Woo_.Models;

namespace Woo_.Services;

public sealed class BuildService
{
    private static readonly Regex AnsiColorRegex = new(@"\x1B\[(?<codes>[0-9;]*)m", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ToolchainService _toolchainService;
    private readonly IconService _iconService;
    private readonly FaviconService _faviconService;
    private readonly DatabaseService _databaseService;
    private readonly AppSettingsService _settingsService;

    public BuildService(
        ToolchainService toolchainService,
        IconService iconService,
        FaviconService faviconService,
        DatabaseService databaseService,
        AppSettingsService settingsService)
    {
        _toolchainService = toolchainService;
        _iconService = iconService;
        _faviconService = faviconService;
        _databaseService = databaseService;
        _settingsService = settingsService;
    }

    public async Task<BuildResult> BuildAsync(
        BuildConfiguration configuration,
        OutputConflictChoice conflictChoice,
        IProgress<BuildLogEntry> progress,
        CancellationToken cancellationToken = default)
    {
        var logBuilder = new StringBuilder();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new ProgressLogSink(progress, logBuilder))
            .CreateLogger();

        var stopwatch = Stopwatch.StartNew();
        var status = "Failed";
        var projectDirectory = string.Empty;

        try
        {
            logger.Information("Starting Woo! build.");

            var validationMessage = Validate(configuration);
            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                logger.Error(validationMessage);
                return CreateResultWithFinalStatus(false, false, validationMessage, projectDirectory, logBuilder, progress);
            }

            var appName = ResolveAppName(configuration);
            configuration.AppName = appName;
            if (configuration.SourceKind == AppSourceKind.Website)
            {
                configuration.WebsiteUrl = NormalizeUrl(configuration.WebsiteUrl);
            }
            if (!FileSystemHelper.TryNormalizeDirectoryPath(configuration.OutputDirectory, out var normalizedOutputDirectory, out var outputPathError))
            {
                logger.Error(outputPathError);
                return CreateResultWithFinalStatus(false, false, outputPathError, projectDirectory, logBuilder, progress);
            }

            configuration.OutputDirectory = normalizedOutputDirectory;
            ApplyFrameworkConstraints(configuration);

            var toolResults = await _toolchainService.VerifyForBuildAsync(configuration.Framework, _settingsService.Settings);
            var missingTools = toolResults.Where(result => !result.IsFound).ToList();
            foreach (var result in toolResults)
            {
                logger.Information(result.DisplayText);
            }

            if (missingTools.Count > 0)
            {
                var missing = string.Join(", ", missingTools.Select(tool => tool.Name));
                var message = $"{missing} not found. Install Node.js from https://nodejs.org/ and Rust from https://www.rust-lang.org/tools/install for Tauri builds, then use Settings > Verify Installation.";
                logger.Error(message);
                return CreateResultWithFinalStatus(false, false, message, projectDirectory, logBuilder, progress);
            }

            Directory.CreateDirectory(configuration.OutputDirectory);
            projectDirectory = Path.Combine(configuration.OutputDirectory, StringSanitizer.ForFileName(appName));
            projectDirectory = PrepareProjectDirectory(projectDirectory, conflictChoice, logger);
            if (string.IsNullOrWhiteSpace(projectDirectory))
            {
                logger.Warning("Build canceled.");
                return CreateResultWithFinalStatus(false, true, "Build canceled.", string.Empty, logBuilder, progress);
            }

            Directory.CreateDirectory(projectDirectory);
            logger.Information("Project folder: {ProjectDirectory}", projectDirectory);

            await ResolveIconAsync(configuration, projectDirectory, logger, cancellationToken);

            if (configuration.Framework == OutputFramework.Electron)
            {
                await ScaffoldElectronAsync(configuration, projectDirectory, logger, cancellationToken);
                await RunProcessAsync(GetNpmExecutable(), "install", projectDirectory, logger, cancellationToken);
                await RunProcessAsync(GetNpmExecutable(), "run build", projectDirectory, logger, cancellationToken);
            }
            else
            {
                await ScaffoldTauriAsync(configuration, projectDirectory, logger, cancellationToken);
                await RunProcessAsync(GetNpmExecutable(), "install", projectDirectory, logger, cancellationToken);
                var tauriBuildCommand = configuration.SingleExecutable
                    ? "run tauri -- build --bundles nsis"
                    : "run tauri -- build --no-bundle";
                await RunProcessAsync(GetNpmExecutable(), tauriBuildCommand, projectDirectory, logger, cancellationToken);
            }

            status = "Success";
            await RunPostBuildActionsAsync(configuration, projectDirectory, logger, cancellationToken);
            return CreateResultWithFinalStatus(true, false, string.Empty, projectDirectory, logBuilder, progress);
        }
        catch (OperationCanceledException)
        {
            status = "Canceled";
            logger.Warning("Build canceled.");
            return CreateResultWithFinalStatus(false, true, "Build canceled.", projectDirectory, logBuilder, progress);
        }
        catch (Exception ex)
        {
            logger.Error(ex.Message);
            return CreateResultWithFinalStatus(false, false, ex.Message, projectDirectory, logBuilder, progress);
        }
        finally
        {
            stopwatch.Stop();
            if (!string.IsNullOrWhiteSpace(GetHistorySource(configuration)) && !string.IsNullOrWhiteSpace(configuration.AppName))
            {
                await SaveHistoryAsync(configuration, projectDirectory, status, logBuilder.ToString(), stopwatch.Elapsed);
            }
        }
    }

    private static string Validate(BuildConfiguration configuration)
    {
        if (configuration.SourceKind == AppSourceKind.Website)
        {
            if (!UrlHelper.TryNormalize(configuration.WebsiteUrl, out _, out var error))
            {
                return error;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(configuration.LocalSourcePath))
            {
                return "Choose an HTML file.";
            }

            if (!File.Exists(configuration.LocalSourcePath))
            {
                return "The selected source file does not exist.";
            }

            var extension = Path.GetExtension(configuration.LocalSourcePath);
            if (configuration.SourceKind == AppSourceKind.HtmlFile &&
                !extension.Equals(".html", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                return "Choose an .html file.";
            }
        }

        if (string.IsNullOrWhiteSpace(configuration.OutputDirectory))
        {
            return "Choose an output directory.";
        }

        if (configuration.WindowWidth < 320 || configuration.WindowHeight < 240)
        {
            return "Window size is too small.";
        }

        return string.Empty;
    }

    private static void ApplyFrameworkConstraints(BuildConfiguration configuration)
    {
        if (configuration.Framework == OutputFramework.Tauri)
        {
            configuration.IncludeAdBlocker = false;
            configuration.SingleExecutable = false;
        }
    }

    private static string ResolveAppName(BuildConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.AppName))
        {
            return StringSanitizer.ForFileName(configuration.AppName);
        }

        if (configuration.SourceKind != AppSourceKind.Website && !string.IsNullOrWhiteSpace(configuration.LocalSourcePath))
        {
            return StringSanitizer.ForFileName(Path.GetFileNameWithoutExtension(configuration.LocalSourcePath));
        }

        return UrlHelper.TryNormalize(configuration.WebsiteUrl, out var uri, out _)
            ? UrlHelper.SuggestName(uri)
            : "Woo App";
    }

    private static string NormalizeUrl(string url)
    {
        return UrlHelper.TryNormalize(url, out var uri, out _) ? uri.ToString() : url;
    }

    private static string PrepareProjectDirectory(string projectDirectory, OutputConflictChoice conflictChoice, ILogger logger)
    {
        if (!Directory.Exists(projectDirectory) || !Directory.EnumerateFileSystemEntries(projectDirectory).Any())
        {
            return projectDirectory;
        }

        return conflictChoice switch
        {
            OutputConflictChoice.Cancel => string.Empty,
            OutputConflictChoice.Rename => FileSystemHelper.GetUniqueDirectory(projectDirectory),
            OutputConflictChoice.Overwrite => DeleteAndReturn(projectDirectory, logger),
            _ => FileSystemHelper.GetUniqueDirectory(projectDirectory)
        };
    }

    private static string DeleteAndReturn(string projectDirectory, ILogger logger)
    {
        logger.Warning("Overwriting existing output folder.");
        StopProcessesInDirectory(projectDirectory, logger);
        DeleteDirectoryWithRetry(projectDirectory);
        return projectDirectory;
    }

    private static void StopProcessesInDirectory(string directory, ILogger logger)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(executable);
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                logger.Warning("Stopping previous build process: {ProcessName}", process.ProcessName);
                process.Kill(true);
                process.WaitForExit(3000);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string directory)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                NormalizeAttributes(directory);
                Directory.Delete(directory, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(250);
            }
        }

        throw new IOException("Could not overwrite the output folder because a previous build is still running or a file is locked. Close the generated app and try again.", lastError);
    }

    private static void NormalizeAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var folder in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(folder, FileAttributes.Directory);
        }

        File.SetAttributes(directory, FileAttributes.Directory);
    }

    private async Task ResolveIconAsync(BuildConfiguration configuration, string projectDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        var iconSource = configuration.ResolvedIconPath;
        var previewSource = configuration.ResolvedIconPath;

        if (!configuration.AutoFetchIcon && !string.IsNullOrWhiteSpace(configuration.CustomIconPath))
        {
            iconSource = configuration.CustomIconPath;
            previewSource = configuration.CustomIconPath;
        }
        else if (configuration.SourceKind == AppSourceKind.Website &&
                 configuration.AutoFetchIcon &&
                 (string.IsNullOrWhiteSpace(iconSource) || !File.Exists(iconSource)))
        {
            logger.Information("Fetching favicon.");
            var metadata = await _faviconService.FetchIconAsync(configuration.WebsiteUrl, cancellationToken);
            iconSource = metadata.IconPath;
            previewSource = metadata.PreviewPngPath;
            configuration.IconUrl = metadata.IconUrl;
        }

        if (string.IsNullOrWhiteSpace(iconSource) || !File.Exists(iconSource))
        {
            var fallback = _faviconService.GetFallbackIcon();
            iconSource = fallback.IconPath ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.ico");
            previewSource = fallback.PreviewPngPath;
        }

        if (configuration.SourceKind == AppSourceKind.Website && configuration.AutoFetchIcon)
        {
            configuration.IconUrl ??= GetFaviconUrl(configuration.WebsiteUrl);
        }

        var iconPath = Path.Combine(projectDirectory, "icon.ico");
        var pngPath = Path.Combine(projectDirectory, "icon.png");
        await _iconService.CreateIcoAsync(iconSource, iconPath, pngPath);
        configuration.ResolvedIconPath = File.Exists(pngPath) ? pngPath : iconPath;

        if (!File.Exists(pngPath) && !string.IsNullOrWhiteSpace(previewSource) && File.Exists(previewSource))
        {
            File.Copy(previewSource, pngPath, true);
        }

        logger.Information("Icon resolved.");
    }

    private async Task ScaffoldElectronAsync(BuildConfiguration configuration, string projectDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        logger.Information("Scaffolding Electron project.");

        var packageName = StringSanitizer.ForPackageName(configuration.AppName);
        var identifier = $"com.woo.{StringSanitizer.ForIdentifierPart(configuration.AppName)}";
        await PreparePackagedSourceAsync(configuration, projectDirectory, "web", logger, cancellationToken);

        var packageJson = new
        {
            name = packageName,
            productName = configuration.AppName,
            version = "1.0.0",
            description = $"Woo! desktop package for {GetDisplaySource(configuration)}",
            main = "main.js",
            scripts = new Dictionary<string, string>
            {
                ["start"] = "electron .",
                ["build"] = configuration.SingleExecutable
                    ? "electron-builder --win portable"
                    : "electron-builder --win dir"
            },
            devDependencies = new Dictionary<string, string>
            {
                ["electron"] = "latest",
                ["electron-builder"] = "latest"
            },
            dependencies = configuration.IncludeAdBlocker
                ? new Dictionary<string, string>
                {
                    ["@ghostery/adblocker-electron"] = "latest",
                    ["cross-fetch"] = "latest"
                }
                : new Dictionary<string, string>(),
            build = new
            {
                appId = identifier,
                productName = configuration.AppName
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "package.json"),
            JsonSerializer.Serialize(packageJson, JsonOptions),
            cancellationToken);

        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "main.js"), CreateElectronMain(configuration), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "index.html"), CreateRedirectHtml(GetLaunchSource(configuration)), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "electron-builder.yml"), CreateElectronBuilderYaml(configuration, identifier), cancellationToken);

        if (configuration.IncludeAdBlocker)
        {
            await WriteUBlockPayloadAsync(projectDirectory, logger, cancellationToken);
            await WriteAdblockFiltersAsync(projectDirectory, cancellationToken);
        }
    }

    private async Task ScaffoldTauriAsync(BuildConfiguration configuration, string projectDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        logger.Information("Scaffolding Tauri project.");

        var identifier = $"com.woo.{StringSanitizer.ForIdentifierPart(configuration.AppName)}";
        var srcDirectory = Path.Combine(projectDirectory, "src");
        var tauriDirectory = Path.Combine(projectDirectory, "src-tauri");
        var tauriSrcDirectory = Path.Combine(tauriDirectory, "src");
        var iconDirectory = Path.Combine(tauriDirectory, "icons");

        Directory.CreateDirectory(srcDirectory);
        Directory.CreateDirectory(tauriSrcDirectory);
        Directory.CreateDirectory(iconDirectory);
        await PreparePackagedSourceAsync(configuration, srcDirectory, string.Empty, logger, cancellationToken);

        File.Copy(Path.Combine(projectDirectory, "icon.ico"), Path.Combine(iconDirectory, "icon.ico"), true);
        File.Copy(Path.Combine(projectDirectory, "icon.png"), Path.Combine(iconDirectory, "icon.png"), true);

        var packageJson = new
        {
            name = StringSanitizer.ForPackageName(configuration.AppName),
            version = "1.0.0",
            type = "module",
            scripts = new Dictionary<string, string>
            {
                ["tauri"] = "tauri"
            },
            devDependencies = new Dictionary<string, string>
            {
                ["@tauri-apps/cli"] = "latest"
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "package.json"),
            JsonSerializer.Serialize(packageJson, JsonOptions),
            cancellationToken);

        if (configuration.SourceKind == AppSourceKind.Website)
        {
            await File.WriteAllTextAsync(Path.Combine(srcDirectory, "index.html"), CreateRedirectHtml(configuration.WebsiteUrl), cancellationToken);
        }
        await File.WriteAllTextAsync(Path.Combine(tauriDirectory, "build.rs"), CreateTauriBuildRs(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(tauriSrcDirectory, "main.rs"), CreateTauriMain(configuration), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(tauriDirectory, "Cargo.toml"), CreateTauriCargo(configuration), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(tauriDirectory, "tauri.conf.json"), CreateTauriConfig(configuration, identifier), cancellationToken);

        if (configuration.IncludeAdBlocker)
        {
            await WriteUBlockPayloadAsync(projectDirectory, logger, cancellationToken);
            logger.Warning("Tauri ad-blocking needs a native request filter plugin; Woo! placed the bundled filter configuration in the project for integration.");
        }
    }

    private static Task PreparePackagedSourceAsync(
        BuildConfiguration configuration,
        string rootDirectory,
        string targetSubDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        configuration.PackagedSourceEntryRelativePath = null;
        if (configuration.SourceKind == AppSourceKind.Website)
        {
            return Task.CompletedTask;
        }

        var sourceRoot = string.IsNullOrWhiteSpace(targetSubDirectory)
            ? rootDirectory
            : Path.Combine(rootDirectory, targetSubDirectory);
        Directory.CreateDirectory(sourceRoot);

        var entryPath = Path.Combine(sourceRoot, "index.html");
        File.Copy(configuration.LocalSourcePath, entryPath, true);
        configuration.PackagedSourceEntryRelativePath = MakeRelativePath(rootDirectory, entryPath);
        logger.Information("Local HTML source copied.");
        return Task.CompletedTask;
    }

    private static string MakeRelativePath(string rootDirectory, string path)
    {
        return Path.GetRelativePath(rootDirectory, path).Replace('\\', '/');
    }

    private static string GetLaunchSource(BuildConfiguration configuration)
    {
        return configuration.SourceKind == AppSourceKind.Website
            ? configuration.WebsiteUrl
            : configuration.LocalSourcePath;
    }

    private static string GetDisplaySource(BuildConfiguration configuration)
    {
        return configuration.SourceKind == AppSourceKind.Website
            ? configuration.WebsiteUrl
            : configuration.LocalSourcePath;
    }

    private static string CreateElectronMain(BuildConfiguration configuration)
    {
        var targetUrlInitializer = configuration.SourceKind == AppSourceKind.Website
            ? JsonSerializer.Serialize(configuration.WebsiteUrl)
            : $"pathToFileURL(path.join(__dirname, {string.Join(", ", (configuration.PackagedSourceEntryRelativePath ?? "index.html").Split('/').Select(part => JsonSerializer.Serialize(part)))})).href";
        var title = JsonSerializer.Serialize(configuration.AppName);
        var userAgent = JsonSerializer.Serialize(configuration.UserAgentOverride ?? string.Empty);
        var adBlockerEnabled = configuration.IncludeAdBlocker ? "true" : "false";
        var devToolsEnabled = configuration.EnableDevTools ? "true" : "false";
        var showMenuBar = configuration.ShowMenuBar ? "true" : "false";
        var allowResizing = configuration.AllowResizing ? "true" : "false";
        var startMaximized = configuration.StartMaximized ? "true" : "false";
        var newLinkRedirect = configuration.NewLinkRedirect ? "true" : "false";
        var persistCookies = configuration.PersistCookies ? "true" : "false";
        var mouseNavigation = configuration.MouseNavigation ? "true" : "false";
        var restrictToMainUrl = configuration.RestrictToMainUrl ? "true" : "false";
        var disableCaching = configuration.DisableCaching ? "true" : "false";
        var allowDownloads = configuration.AllowDownloads ? "true" : "false";
        var systemTray = configuration.Framework == OutputFramework.Electron && configuration.SystemTray ? "true" : "false";
        var persistentPartition = JsonSerializer.Serialize($"persist:woo-{StringSanitizer.ForPackageName(configuration.AppName)}");
        var appUserModelId = JsonSerializer.Serialize($"com.woo.{StringSanitizer.ForIdentifierPart(configuration.AppName)}");

        return $$"""
            const { app, BrowserWindow, session, Tray, Menu } = require('electron');
            const fs = require('fs');
            const path = require('path');
            const { pathToFileURL } = require('url');

            const targetUrl = {{targetUrlInitializer}};
            const configuredTitle = {{title}};
            const configuredUserAgent = {{userAgent}};
            const persistCookies = {{persistCookies}};
            const adBlockerEnabled = {{adBlockerEnabled}};
            const newLinkRedirect = {{newLinkRedirect}};
            const mouseNavigation = {{mouseNavigation}};
            const restrictToMainUrl = {{restrictToMainUrl}};
            const disableCaching = {{disableCaching}};
            const allowDownloads = {{allowDownloads}};
            const systemTray = {{systemTray}};
            const persistentPartition = {{persistentPartition}};
            const appUserModelId = {{appUserModelId}};
            let tray = null;
            let isQuitting = false;
            const adBlockPatterns = [
              /(^|\/\/)([^/]+\.)?doubleclick\.net\//i,
              /(^|\/\/)([^/]+\.)?googlesyndication\.com\//i,
              /(^|\/\/)([^/]+\.)?googleadservices\.com\//i,
              /(^|\/\/)([^/]+\.)?adservice\.google\./i,
              /(^|\/\/)([^/]+\.)?adsystem\.com\//i,
              /(^|\/\/)([^/]+\.)?adnxs\.com\//i,
              /(^|\/\/)([^/]+\.)?adsafeprotected\.com\//i,
              /(^|\/\/)([^/]+\.)?amazon-adsystem\.com\//i,
              /(^|\/\/)([^/]+\.)?criteo\.com\//i,
              /(^|\/\/)([^/]+\.)?criteo\.net\//i,
              /(^|\/\/)([^/]+\.)?outbrain\.com\//i,
              /(^|\/\/)([^/]+\.)?taboola\.com\//i,
              /(^|\/\/)([^/]+\.)?scorecardresearch\.com\//i,
              /(^|\/\/)([^/]+\.)?hotjar\.com\//i,
              /(^|\/\/)([^/]+\.)?clarity\.ms\//i,
              /(^|\/\/)([^/]+\.)?facebook\.com\/tr\?/i,
              /(^|\/\/)([^/]+\.)?analytics\.google\.com\//i,
              /(^|\/\/)([^/]+\.)?googletagmanager\.com\//i,
              /(^|\/\/)([^/]+\.)?googletagservices\.com\//i,
              /(^|\/\/)([^/]+\.)?pubmatic\.com\//i,
              /(^|\/\/)([^/]+\.)?rubiconproject\.com\//i,
              /(^|\/\/)([^/]+\.)?openx\.net\//i,
              /(^|\/\/)([^/]+\.)?serving-sys\.com\//i,
              /(^|\/\/)([^/]+\.)?moatads\.com\//i,
              /(^|\/\/)([^/]+\.)?quantserve\.com\//i,
              /(^|\/\/)([^/]+\.)?media\.net\//i,
              /\/pagead\.js(\?|$)/i,
              /\/ads\.js(\?|$)/i,
              /\/widget\/ads\./i,
              /\/adframe[\/\._-]/i,
              /\/adserver[\/\._-]/i,
              /\/adservice[\/\._-]/i,
              /\/banner(s)?[\/\._-]/i,
              /\/popunder[\/\._-]/i,
              /\/tracking[\/\._-]/i,
              /\/ads?[\/\.-]/i,
              /\/advert(s|ising)?[\/\.-]/i,
              /\/sponsor(ed)?[\/\.-]/i,
              /[?&](ad|ads|adid|adurl|advertising_id)=/i
            ];
            const wooAdblockers = [];

            function normalizeComparableUrl(value) {
              const parsed = new URL(value);
              parsed.hash = '';
              return parsed.href;
            }

            const allowedMainUrl = normalizeComparableUrl(targetUrl);

            function isMainUrl(value) {
              try {
                return normalizeComparableUrl(value) === allowedMainUrl;
              } catch {
                return false;
              }
            }

            function getHostname(value) {
              try {
                return new URL(value).hostname.toLowerCase();
              } catch {
                return '';
              }
            }

            function isYouTubeHost(host) {
              return host === 'youtube.com' ||
                host.endsWith('.youtube.com') ||
                host === 'youtu.be' ||
                host.endsWith('.youtu.be') ||
                host === 'googlevideo.com' ||
                host.endsWith('.googlevideo.com') ||
                host === 'ytimg.com' ||
                host.endsWith('.ytimg.com');
            }

            function isYouTubePlaybackRequest(details) {
              const host = getHostname(details.url);
              return host.endsWith('googlevideo.com') ||
                host.endsWith('ytimg.com') ||
                details.resourceType === 'media' ||
                details.url.includes('/videoplayback');
            }

            function isYouTubeAdRequest(details) {
              const value = details.url.toLowerCase();
              const host = getHostname(value);
              const isGoogleAdHost = host.endsWith('doubleclick.net') ||
                host.endsWith('googlesyndication.com') ||
                host.endsWith('googleadservices.com') ||
                host.endsWith('googletagservices.com');

              if (isGoogleAdHost) {
                return true;
              }

              if (!isYouTubeHost(host)) {
                return false;
              }

              if (isYouTubePlaybackRequest(details)) {
                return /[?&](oad|adformat|ad_type|adurl|afv|ad_preroll|ad_break|adunit|ctier)=/i.test(value);
              }

              return value.includes('/pagead/') ||
                value.includes('/get_midroll_info') ||
                value.includes('/api/stats/ads') ||
                value.includes('/ptracking') ||
                value.includes('adformat=') ||
                value.includes('adunit') ||
                value.includes('ad_break') ||
                value.includes('adplacements') ||
                value.includes('ad_placement');
            }

            function loadCustomAdblockRules() {
              const rules = {
                hosts: [],
                substrings: []
              };
              const customFiltersPath = path.join(__dirname, 'filters', 'custom.txt');
              if (!fs.existsSync(customFiltersPath)) {
                return rules;
              }

              const lines = fs.readFileSync(customFiltersPath, 'utf8').split(/\r?\n/g);
              for (const rawLine of lines) {
                const line = rawLine.replace(/\s+\/\/.*$/, '').trim();
                if (!line || line.startsWith('!') || line.startsWith('@@') || line.includes('##') || line.includes('#@#')) {
                  continue;
                }

                const networkRule = line.split('$')[0].replace(/\*/g, '');
                if (networkRule.startsWith('||')) {
                  const body = networkRule.slice(2);
                  const host = body.split('^')[0].split('/')[0].trim().toLowerCase();
                  if (host && !host.includes('*')) {
                    rules.hosts.push(host);
                  }
                  continue;
                }

                const cleaned = networkRule.replace(/\^/g, '').trim();
                if (cleaned.length > 4) {
                  rules.substrings.push(cleaned);
                }
              }

              return rules;
            }

            const customAdblockRules = loadCustomAdblockRules();
            const requestFilter = { urls: ['http://*/*', 'https://*/*'] };

            function loadCustomCosmeticSelectors() {
              const customFiltersPath = path.join(__dirname, 'filters', 'custom.txt');
              if (!fs.existsSync(customFiltersPath)) {
                return [];
              }

              return fs.readFileSync(customFiltersPath, 'utf8')
                .split(/\r?\n/g)
                .map((line) => line.trim())
                .filter((line) => line && line.includes('##') && !line.includes('#@#'))
                .map((line) => line.split('##').slice(1).join('##').trim())
                .filter((selector) => selector.length > 0);
            }

            function buildCosmeticCss() {
              const commonSelectors = [
                '.adsbygoogle',
                '.ad-banner',
                '.adbox',
                '.adsbox',
                '.adblock',
                '.ad-container',
                '.ad-placeholder',
                '.ad-slot',
                '.ad_space',
                '.adunit',
                '.ad-wrapper',
                '.advert',
                '.advertisement',
                '.advertising',
                '.banner_ads',
                '.google-auto-placed',
                '.sponsored',
                '.textads',
                '[data-ad]',
                '[data-ad-client]',
                '[data-ad-slot]',
                '[data-ad-unit]',
                '[id^="ad-"]',
                '[id*="-ad-"]',
                '[id*="_ad_"]',
                '[id*="ads-"]',
                '[id*="advert"]',
                '[class^="ad-"]',
                '[class*=" ad-"]',
                '[class*="-ad-"]',
                '[class*="_ad_"]',
                '[class*="ads-"]',
                '[class*="banner_ads"]',
                '[class*="advert"]',
                'iframe[src*="doubleclick.net"]',
                'iframe[src*="googlesyndication.com"]',
                'iframe[src*="googleadservices.com"]',
                'iframe[src*="googletagservices.com"]',
                'iframe[src*="adnxs.com"]',
                'iframe[src*="amazon-adsystem.com"]',
                'iframe[src*="criteo."]',
                'iframe[src*="outbrain.com"]',
                'iframe[src*="taboola.com"]'
              ];

              return [...commonSelectors, ...loadCustomCosmeticSelectors()]
                .map((selector) => `${selector} { display: none !important; visibility: hidden !important; pointer-events: none !important; }`)
                .join('\n');
            }

            function isAdRequest(value) {
              if (adBlockPatterns.some((pattern) => pattern.test(value))) {
                return true;
              }

              try {
                const parsed = new URL(value);
                const host = parsed.hostname.toLowerCase();
                if (customAdblockRules.hosts.some((ruleHost) => host === ruleHost || host.endsWith(`.${ruleHost}`))) {
                  return true;
                }
              } catch {
                return false;
              }

              return customAdblockRules.substrings.some((substring) => value.includes(substring));
            }

            async function getEngineBlockResponse(details) {
              for (const entry of wooAdblockers) {
                const response = await Promise.race([
                  new Promise((resolve) => {
                    try {
                      entry.blocker.onBeforeRequest(details, (result) => resolve(result || { cancel: false }));
                    } catch (error) {
                      console.warn(`[Woo] ${entry.name} request filter failed: ${error.message}`);
                      resolve({ cancel: false });
                    }
                  }),
                  new Promise((resolve) => setTimeout(() => resolve({ cancel: false }), 750))
                ]);

                if (response && (response.cancel || response.redirectURL)) {
                  console.log(`[Woo ${entry.name} BLOCKED]`, details.url);
                  return response;
                }
              }

              return null;
            }

            async function getEngineHeadersResponse(details) {
              let response = {};
              for (const entry of wooAdblockers) {
                const engineResponse = await Promise.race([
                  new Promise((resolve) => {
                    try {
                      entry.blocker.onHeadersReceived(details, (result) => resolve(result || {}));
                    } catch (error) {
                      console.warn(`[Woo] ${entry.name} header filter failed: ${error.message}`);
                      resolve({});
                    }
                  }),
                  new Promise((resolve) => setTimeout(() => resolve({}), 750))
                ]);

                if (engineResponse.responseHeaders) {
                  response = { ...response, responseHeaders: engineResponse.responseHeaders };
                  details = { ...details, responseHeaders: engineResponse.responseHeaders };
                }
              }

              return response;
            }

            function installRequestGuards(appSession) {
              appSession.webRequest.onBeforeRequest(requestFilter, async (details, callback) => {
                if (restrictToMainUrl && details.resourceType === 'mainFrame' && !isMainUrl(details.url)) {
                  console.log('[Woo] Blocked navigation:', details.url);
                  callback({ cancel: true });
                  return;
                }

                if (adBlockerEnabled && isYouTubeAdRequest(details)) {
                  console.log('[Woo YouTube BLOCKED]', details.url);
                  callback({ cancel: true });
                  return;
                }

                if (adBlockerEnabled && isYouTubePlaybackRequest(details)) {
                  callback({ cancel: false });
                  return;
                }

                if (adBlockerEnabled && wooAdblockers.length > 0) {
                  const engineResponse = await getEngineBlockResponse(details);
                  if (engineResponse) {
                    callback(engineResponse);
                    return;
                  }
                }

                if (adBlockerEnabled && isAdRequest(details.url)) {
                  console.log('[Woo BLOCKED]', details.url);
                  callback({ cancel: true });
                  return;
                }

                callback({ cancel: false });
              });

              if (adBlockerEnabled || disableCaching) {
                appSession.webRequest.onHeadersReceived(requestFilter, async (details, callback) => {
                  if (adBlockerEnabled && isYouTubePlaybackRequest(details)) {
                    callback({});
                    return;
                  }

                  const engineResponse = adBlockerEnabled && wooAdblockers.length > 0
                    ? await getEngineHeadersResponse(details)
                    : {};
                  const baseResponseHeaders = engineResponse.responseHeaders || details.responseHeaders || {};

                  if (!disableCaching) {
                    callback(engineResponse);
                    return;
                  }

                  const responseHeaders = {
                    ...baseResponseHeaders,
                    'Cache-Control': ['no-store, no-cache, must-revalidate, proxy-revalidate'],
                    Pragma: ['no-cache'],
                    Expires: ['0']
                  };
                  callback({ responseHeaders });
                });
              }

              if (adBlockerEnabled) {
                console.log('[Woo] Built-in adblock enabled.');
              }
            }

            function installCosmeticAdBlocker(win) {
              if (!adBlockerEnabled) {
                return;
              }

              const cosmeticCss = buildCosmeticCss();
              if (!cosmeticCss) {
                return;
              }

              const inject = () => {
                if (isYouTubeHost(getHostname(win.webContents.getURL()))) {
                  return;
                }

                win.webContents.insertCSS(cosmeticCss, { cssOrigin: 'user' })
                  .catch((error) => console.warn(`Woo cosmetic adblock CSS failed: ${error.message}`));
              };

              win.webContents.on('dom-ready', inject);
              win.webContents.on('did-navigate', inject);
              win.webContents.on('did-navigate-in-page', inject);
              console.log('[Woo] Cosmetic adblock enabled.');
            }

            function installYouTubeAdHelper(win) {
              if (!adBlockerEnabled) {
                return;
              }

              const css = `
                #player-ads,
                #masthead-ad,
                ytd-display-ad-renderer,
                ytd-promoted-sparkles-web-renderer,
                ytd-ad-slot-renderer,
                .ytp-ad-overlay-container {
                  display: none !important;
                  visibility: hidden !important;
                }
              `;

              const script = `
                (() => {
                  if (window.__wooYouTubeAdHelper) return;
                  window.__wooYouTubeAdHelper = true;
                  const clickSkipIfVisible = () => {
                    const skipButton = document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button');
                    if (!skipButton) return;

                    const rect = skipButton.getBoundingClientRect();
                    const style = getComputedStyle(skipButton);
                    if (rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none') {
                      skipButton.click();
                      setTimeout(() => {
                        const video = document.querySelector('video');
                        if (video && typeof video.play === 'function') {
                          const playPromise = video.play();
                          if (playPromise && typeof playPromise.catch === 'function') {
                            playPromise.catch(() => {});
                          }
                        }
                      }, 250);
                    }
                  };

                  setInterval(() => {
                    clickSkipIfVisible();
                    for (const selector of ['#player-ads', '#masthead-ad', 'ytd-display-ad-renderer', 'ytd-promoted-sparkles-web-renderer', 'ytd-ad-slot-renderer', '.ytp-ad-overlay-container']) {
                      document.querySelectorAll(selector).forEach((node) => node.remove());
                    }
                  }, 700);
                })();
              `;

              const inject = () => {
                if (!isYouTubeHost(getHostname(win.webContents.getURL()))) {
                  return;
                }

                win.webContents.insertCSS(css, { cssOrigin: 'user' })
                  .catch((error) => console.warn(`Woo YouTube ad CSS failed: ${error.message}`));
                win.webContents.executeJavaScript(script, true)
                  .catch((error) => console.warn(`Woo YouTube ad helper failed: ${error.message}`));
              };

              win.webContents.on('dom-ready', inject);
              win.webContents.on('did-navigate', inject);
              win.webContents.on('did-navigate-in-page', inject);
            }

            async function setupGhosteryAdblocker(appSession) {
              try {
                const { ElectronBlocker } = require('@ghostery/adblocker-electron');
                const fetchModule = require('cross-fetch');
                const fetch = fetchModule.default || fetchModule;
                const blocker = await ElectronBlocker.fromPrebuiltAdsAndTracking(fetch, {
                  path: path.join(app.getPath('userData'), 'adblock-engine.bin'),
                  read: fs.promises.readFile,
                  write: fs.promises.writeFile
                });

                wooAdblockers.push({ name: 'Ghostery', blocker });
                console.log('[Woo] Ghostery adblock engine loaded.');
                return true;
              } catch (error) {
                console.warn(`Ghostery adblocker could not be enabled: ${error.message}`);
                return false;
              }
            }

            async function setupCustomAdblocker(appSession) {
              try {
                const { ElectronBlocker } = require('@ghostery/adblocker-electron');
                const customFiltersPath = path.join(__dirname, 'filters', 'custom.txt');
                if (!fs.existsSync(customFiltersPath)) {
                  return false;
                }

                const rawFilters = fs.readFileSync(customFiltersPath, 'utf8');
                const blocker = ElectronBlocker.parse(rawFilters);

                wooAdblockers.push({ name: 'Custom', blocker });
                console.log('[Woo] Custom adblock filters loaded.');
                return true;
              } catch (error) {
                console.warn(`Custom adblock filters could not be enabled: ${error.message}`);
                return false;
              }
            }

            async function setupTurtlecuteHostList() {
              try {
                const { ElectronBlocker } = require('@ghostery/adblocker-electron');
                const fetchModule = require('cross-fetch');
                const fetch = fetchModule.default || fetchModule;
                const response = await fetch('https://raw.githubusercontent.com/Turtlecute33/toolz/master/src/d3host.adblock');
                if (!response.ok) {
                  throw new Error(`HTTP ${response.status}`);
                }

                const rawFilters = await response.text();
                const blocker = ElectronBlocker.parse(rawFilters);
                wooAdblockers.push({ name: 'Turtlecute', blocker });
                console.log('[Woo] Turtlecute host list loaded.');
                return true;
              } catch (error) {
                console.warn(`Turtlecute host list could not be enabled: ${error.message}`);
                return false;
              }
            }

            function resolveUBlockPath() {
              const candidates = [
                path.join(__dirname, 'extensions', 'ublock'),
                path.join(process.resourcesPath || '', 'extensions', 'ublock'),
                path.join(process.resourcesPath || '', 'app', 'extensions', 'ublock')
              ];

              return candidates.find((candidate) => fs.existsSync(path.join(candidate, 'manifest.json')));
            }

            const preloadUBlockPath = adBlockerEnabled ? resolveUBlockPath() : null;
            if (disableCaching) {
              app.commandLine.appendSwitch('disable-http-cache');
            }

            app.setAppUserModelId(appUserModelId);

            if (preloadUBlockPath) {
              app.commandLine.appendSwitch('disable-extensions-except', preloadUBlockPath);
              app.commandLine.appendSwitch('load-extension', preloadUBlockPath);
            }

            function installTray(win) {
              if (!systemTray) {
                return;
              }

              tray = new Tray(path.join(__dirname, 'icon.ico'));
              tray.setToolTip(configuredTitle);
              tray.setContextMenu(Menu.buildFromTemplate([
                { label: 'Show', click: () => { win.show(); win.focus(); } },
                { label: 'Quit', click: () => { isQuitting = true; app.quit(); } }
              ]));
              tray.on('double-click', () => {
                win.show();
                win.focus();
              });

              win.on('close', (event) => {
                if (!isQuitting) {
                  event.preventDefault();
                  win.hide();
                }
              });
            }

            async function createWindow() {
              const partition = (persistCookies || adBlockerEnabled) ? persistentPartition : `temporary:${Date.now()}`;
              const appSession = session.fromPartition(partition);

              if (!persistCookies) {
                app.on('before-quit', () => {
                  appSession.clearStorageData().catch((error) => console.warn(`Could not clear storage data: ${error.message}`));
                  appSession.clearCache().catch((error) => console.warn(`Could not clear cache: ${error.message}`));
                });
              }

              if (disableCaching) {
                await appSession.clearCache().catch((error) => console.warn(`Could not clear cache: ${error.message}`));
              }

              if (configuredUserAgent) {
                appSession.setUserAgent(configuredUserAgent);
              }

              appSession.on('will-download', (event, item) => {
                if (!allowDownloads) {
                  event.preventDefault();
                  console.log('[Woo] Download blocked:', item.getURL ? item.getURL() : item.getFilename());
                  return;
                }

                try {
                  item.setSavePath(path.join(app.getPath('downloads'), item.getFilename()));
                } catch {
                }
              });

              if (adBlockerEnabled) {
                await setupGhosteryAdblocker(appSession);
                await setupCustomAdblocker(appSession);
                await setupTurtlecuteHostList();

                try {
                  const uBlockPath = resolveUBlockPath();
                  if (!uBlockPath) {
                    throw new Error('uBlock Origin extension folder was not found in packaged resources.');
                  }

                  const loadedExtension = appSession.extensions && appSession.extensions.loadExtension
                    ? await appSession.extensions.loadExtension(uBlockPath, { allowFileAccess: true })
                    : await appSession.loadExtension(uBlockPath, { allowFileAccess: true });
                  console.log(`[Woo] uBlock Origin extension loaded: ${loadedExtension.name || loadedExtension.id}`);
                } catch (error) {
                  console.warn(`uBlock Origin extension could not be loaded: ${error.message}`);
                }
              }

              installRequestGuards(appSession);

              const win = new BrowserWindow({
                width: {{configuration.WindowWidth}},
                height: {{configuration.WindowHeight}},
                minWidth: 320,
                minHeight: 240,
                resizable: {{allowResizing}},
                show: true,
                title: configuredTitle,
                icon: path.join(__dirname, 'icon.ico'),
                autoHideMenuBar: !{{showMenuBar}},
                webPreferences: {
                  devTools: {{devToolsEnabled}},
                  contextIsolation: true,
                  nodeIntegration: false,
                  sandbox: true,
                  partition
                }
              });

              if ({{startMaximized}}) {
                win.maximize();
              }

              const createChildWindow = (url) => {
                const child = new BrowserWindow({
                  width: 1100,
                  height: 780,
                  title: configuredTitle,
                  icon: path.join(__dirname, 'icon.ico'),
                  autoHideMenuBar: !{{showMenuBar}},
                  webPreferences: {
                    devTools: {{devToolsEnabled}},
                    contextIsolation: true,
                    nodeIntegration: false,
                    sandbox: true,
                    session: appSession
                  }
                });

                installCosmeticAdBlocker(child);
                installYouTubeAdHelper(child);
                child.webContents.setWindowOpenHandler(({ url: childUrl }) => {
                  if (restrictToMainUrl && !isMainUrl(childUrl)) {
                    return { action: 'deny' };
                  }

                  if (newLinkRedirect) {
                    child.loadURL(childUrl);
                    return { action: 'deny' };
                  }

                  createChildWindow(childUrl);
                  return { action: 'deny' };
                });
                child.loadURL(url);
                return child;
              };

              win.webContents.setWindowOpenHandler(({ url }) => {
                if (restrictToMainUrl && !isMainUrl(url)) {
                  return { action: 'deny' };
                }

                if (newLinkRedirect) {
                  win.loadURL(url);
                  return { action: 'deny' };
                }

                createChildWindow(url);
                return { action: 'deny' };
              });

              win.webContents.on('will-navigate', (event, url) => {
                if (restrictToMainUrl && !isMainUrl(url)) {
                  event.preventDefault();
                }
              });

              if (mouseNavigation) {
                win.on('app-command', (event, command) => {
                  if (command === 'browser-backward' && win.webContents.canGoBack()) {
                    event.preventDefault();
                    win.webContents.goBack();
                  } else if (command === 'browser-forward' && win.webContents.canGoForward()) {
                    event.preventDefault();
                    win.webContents.goForward();
                  }
                });
              }

              installCosmeticAdBlocker(win);
              installYouTubeAdHelper(win);
              installTray(win);

              await win.loadURL(targetUrl);
            }

            app.whenReady().then(createWindow);

            app.on('before-quit', () => {
              isQuitting = true;
            });

            app.on('window-all-closed', () => {
              if (process.platform !== 'darwin') {
                app.quit();
              }
            });

            app.on('activate', () => {
              if (BrowserWindow.getAllWindows().length === 0) {
                createWindow();
              }
            });
            """;
    }

    private static string CreateElectronBuilderYaml(BuildConfiguration configuration, string identifier)
    {
        var target = configuration.SingleExecutable ? "portable" : "dir";
        var portableBlock = configuration.SingleExecutable
            ? """
              portable:
                artifactName: "${productName}.exe"
              """
            : string.Empty;
        var extraResourcesBlock = configuration.IncludeAdBlocker
            ? """
              extraResources:
                - from: extensions/ublock
                  to: extensions/ublock
                  filter:
                    - "**/*"
              """
            : string.Empty;

        return $$"""
            appId: {{identifier}}
            productName: "{{configuration.AppName.Replace("\"", "\\\"")}}"
            asar: false
            directories:
              output: dist
            files:
              - "**/*"
              - "!dist/**"
            {{extraResourcesBlock}}
            win:
              icon: icon.ico
              target:
                - target: {{target}}
                  arch:
                    - x64
            {{portableBlock}}
            """;
    }

    private static string CreateRedirectHtml(string websiteUrl)
    {
        var encodedUrl = System.Net.WebUtility.HtmlEncode(websiteUrl);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>Woo!</title>
              <meta http-equiv="refresh" content="0; url={{encodedUrl}}">
            </head>
            <body>
              <a href="{{encodedUrl}}">Open packaged website</a>
            </body>
            </html>
            """;
    }

    private static string CreateTauriMain(BuildConfiguration configuration)
    {
        var navigationTarget = JsonSerializer.Serialize(configuration.SourceKind == AppSourceKind.Website ? configuration.WebsiteUrl : "file:///index.html");
        var restrictToMainUrl = configuration.RestrictToMainUrl ? "true" : "false";
        var newLinkRedirect = configuration.NewLinkRedirect ? "true" : "false";

        return $$"""
            #![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

            use std::sync::Arc;
            use std::sync::atomic::{AtomicUsize, Ordering};
            use tauri::{Manager, WebviewWindowBuilder, utils::config::WebviewUrl};
            use tauri::webview::NewWindowResponse;

            const TARGET_URL: &str = {{navigationTarget}};
            const RESTRICT_TO_MAIN_URL: bool = {{restrictToMainUrl}};
            const NEW_LINK_REDIRECT: bool = {{newLinkRedirect}};

            fn is_allowed_navigation(url: &url::Url, target: &url::Url) -> bool {
                if !RESTRICT_TO_MAIN_URL {
                    return true;
                }

                let mut current = url.clone();
                let mut expected = target.clone();
                current.set_fragment(None);
                expected.set_fragment(None);
                current == expected
            }

            fn main() {
                tauri::Builder::default()
                    .setup(|app| {
                        let target_url = url::Url::parse(TARGET_URL)?;
                        let navigation_target = target_url.clone();
                        let new_window_target = target_url.clone();
                        let app_handle = app.handle().clone();
                        let window_counter = Arc::new(AtomicUsize::new(1));
                        let new_window_counter = window_counter.clone();
                        let window_config = app.config().app.windows.first()
                            .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::NotFound, "missing main window config"))?
                            .clone();

                        WebviewWindowBuilder::from_config(app.handle(), &window_config)?
                            .on_navigation(move |url| is_allowed_navigation(url, &navigation_target))
                            .on_new_window(move |url, features| {
                                if !is_allowed_navigation(&url, &new_window_target) {
                                    return NewWindowResponse::Deny;
                                }

                                if NEW_LINK_REDIRECT {
                                    if let Some(window) = app_handle.get_webview_window("main") {
                                        let _ = window.navigate(url);
                                    }

                                    return NewWindowResponse::Deny;
                                }

                                let label = format!("woo-child-{}", new_window_counter.fetch_add(1, Ordering::Relaxed));
                                let child_navigation_target = new_window_target.clone();
                                let window = match WebviewWindowBuilder::new(&app_handle, label, WebviewUrl::External(url.clone()))
                                    .title(url.as_str())
                                    .window_features(features)
                                    .on_navigation(move |next_url| is_allowed_navigation(next_url, &child_navigation_target))
                                    .build()
                                {
                                    Ok(window) => window,
                                    Err(_) => return NewWindowResponse::Deny
                                };

                                NewWindowResponse::Create { window }
                            })
                            .build()?;

                        Ok(())
                    })
                    .run(tauri::generate_context!())
                    .expect("error while running tauri application");
            }
            """;
    }

    private static string CreateTauriBuildRs()
    {
        return """
            fn main() {
                tauri_build::build()
            }
            """;
    }

    private static string CreateTauriCargo(BuildConfiguration configuration)
    {
        var packageName = StringSanitizer.ForPackageName(configuration.AppName).Replace("-", "_");
        return $$"""
            [package]
            name = "{{packageName}}"
            version = "1.0.0"
            description = "Woo! desktop package"
            authors = ["Woo!"]
            edition = "2021"

            [build-dependencies]
            tauri-build = { version = "2", features = [] }

            [dependencies]
            tauri = { version = "2", features = [] }
            serde = { version = "1", features = ["derive"] }
            serde_json = "1"
            url = "2"
            """;
    }

    private static string CreateTauriConfig(BuildConfiguration configuration, string identifier)
    {
        var windowUrl = configuration.SourceKind == AppSourceKind.Website
            ? configuration.WebsiteUrl
            : configuration.PackagedSourceEntryRelativePath ?? "index.html";

        var window = new Dictionary<string, object?>
        {
            ["label"] = "main",
            ["create"] = false,
            ["title"] = configuration.AppName,
            ["url"] = windowUrl,
            ["width"] = configuration.WindowWidth,
            ["height"] = configuration.WindowHeight,
            ["resizable"] = configuration.AllowResizing
        };

        if (!string.IsNullOrWhiteSpace(configuration.UserAgentOverride))
        {
            window["userAgent"] = configuration.UserAgentOverride;
        }

        var config = new Dictionary<string, object?>
        {
            ["$schema"] = "https://schema.tauri.app/config/2",
            ["productName"] = configuration.AppName,
            ["version"] = "1.0.0",
            ["identifier"] = identifier,
            ["build"] = new Dictionary<string, object?>
            {
                ["frontendDist"] = "../src"
            },
            ["app"] = new Dictionary<string, object?>
            {
                ["windows"] = new[] { window },
                ["security"] = new Dictionary<string, object?>
                {
                    ["csp"] = null
                }
            },
            ["bundle"] = new Dictionary<string, object?>
            {
                ["active"] = configuration.SingleExecutable,
                ["targets"] = new[] { "nsis" },
                ["icon"] = new[] { "icons/icon.ico", "icons/icon.png" }
            }
        };

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static async Task WriteAdblockFiltersAsync(string projectDirectory, CancellationToken cancellationToken)
    {
        var filtersDirectory = Path.Combine(projectDirectory, "filters");
        Directory.CreateDirectory(filtersDirectory);

        var filters = new List<string>
        {
            "! Woo! built-in adblock filters",
            "||doubleclick.net^",
            "||googlesyndication.com^",
            "||googleadservices.com^",
            "||adservice.google.*^",
            "||googletagmanager.com^",
            "||analytics.google.com^",
            "||facebook.com/tr^",
            "adblock.turtlecute.org##.adbox.banner_ads.adsbox",
            "adblock.turtlecute.org##.textads",
            "/pagead.js$domain=adblock.turtlecute.org",
            "/widget/ads.",
            "/ads.js",
            "/adserver/",
            "/pagead.js",
            "/pagead/",
            "/get_midroll_info",
            "/api/stats/ads",
            "/ptracking",
            "adformat=",
            "adunit",
            "ad_break",
            "adplacements",
            "ad_placement"
        };

        var sourceConfig = Path.Combine(AppContext.BaseDirectory, "Assets", "ublock-config.json");
        if (File.Exists(sourceConfig))
        {
            try
            {
                var configJson = await File.ReadAllTextAsync(sourceConfig, cancellationToken);
                using var document = JsonDocument.Parse(configJson);
                if (document.RootElement.TryGetProperty("userFilters", out var userFilters))
                {
                    filters.Add(string.Empty);
                    filters.Add("! Woo! uBlock user filters");
                    filters.Add(userFilters.GetString() ?? string.Empty);
                }

                if (document.RootElement.TryGetProperty("whitelist", out var whitelist) &&
                    whitelist.ValueKind == JsonValueKind.Array)
                {
                    filters.Add(string.Empty);
                    filters.Add("! Woo! trusted sites");
                    foreach (var entry in whitelist.EnumerateArray())
                    {
                        var host = entry.GetString();
                        if (!string.IsNullOrWhiteSpace(host) &&
                            !host.Contains('/') &&
                            !IsYouTubeWhitelistHost(host))
                        {
                            filters.Add($"@@||{host}^$document");
                        }
                    }
                }
            }
            catch
            {
                filters.Add("! Could not parse bundled uBlock configuration.");
            }
        }

        await File.WriteAllLinesAsync(Path.Combine(filtersDirectory, "custom.txt"), filters, cancellationToken);
    }

    private static async Task WriteUBlockPayloadAsync(string projectDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        var extensionDirectory = Path.Combine(projectDirectory, "extensions", "ublock");
        Directory.CreateDirectory(extensionDirectory);

        await CopyOrDownloadUBlockSourceAsync(extensionDirectory, logger, cancellationToken);

        var sourceConfig = Path.Combine(AppContext.BaseDirectory, "Assets", "ublock-config.json");
        var targetConfig = Path.Combine(extensionDirectory, "ublock-config.json");
        if (File.Exists(sourceConfig))
        {
            var configJson = await File.ReadAllTextAsync(sourceConfig, cancellationToken);
            configJson = RemoveYouTubeFromUBlockWhitelist(configJson);
            await File.WriteAllTextAsync(targetConfig, configJson, cancellationToken);
            await WriteUBlockConfigFilesAsync(extensionDirectory, targetConfig, cancellationToken);
        }

        if (!File.Exists(Path.Combine(extensionDirectory, "manifest.json")))
        {
            await File.WriteAllTextAsync(
                Path.Combine(extensionDirectory, "README.txt"),
                "Woo! could not download the Chromium-compatible uBlock Origin source. Place the unpacked extension source in this folder before packaging.",
                cancellationToken);
        }

        logger.Information("uBlock Origin configuration written.");
    }

    private static async Task WriteUBlockConfigFilesAsync(string extensionDirectory, string configPath, CancellationToken cancellationToken)
    {
        var configJson = await File.ReadAllTextAsync(configPath, cancellationToken);
        var userDirectory = Path.Combine(extensionDirectory, "assets", "user");
        Directory.CreateDirectory(userDirectory);

        await File.WriteAllTextAsync(Path.Combine(userDirectory, "ublock-config.json"), configJson, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(userDirectory, "backup.json"), configJson, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.TryGetProperty("userFilters", out var userFilters))
            {
                await File.WriteAllTextAsync(Path.Combine(userDirectory, "filters.txt"), userFilters.GetString() ?? string.Empty, cancellationToken);
            }
        }
        catch
        {
        }

        var managedStorage = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["adminSettings"] = configJson
        }, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "managed-storage.json"), managedStorage, cancellationToken);
    }

    private static string RemoveYouTubeFromUBlockWhitelist(string configJson)
    {
        try
        {
            var root = JsonNode.Parse(configJson)?.AsObject();
            if (root?["whitelist"] is JsonArray whitelist)
            {
                for (var i = whitelist.Count - 1; i >= 0; i--)
                {
                    var host = whitelist[i]?.GetValue<string>();
                    if (host is not null && IsYouTubeWhitelistHost(host))
                    {
                        whitelist.RemoveAt(i);
                    }
                }
            }

            return root?.ToJsonString(JsonOptions) ?? configJson;
        }
        catch
        {
            return configJson;
        }
    }

    private static bool IsYouTubeWhitelistHost(string host)
    {
        var normalized = host.Trim().ToLowerInvariant();
        return normalized == "youtube.com" ||
               normalized == "www.youtube.com" ||
               normalized.EndsWith(".youtube.com", StringComparison.Ordinal) ||
               normalized == "youtu.be" ||
               normalized.EndsWith(".youtu.be", StringComparison.Ordinal);
    }

    private static async Task CopyOrDownloadUBlockSourceAsync(string extensionDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        const string version = "1.71.0";
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Woo!",
            "ublock",
            version);

        var cachedManifest = Directory.Exists(cacheDirectory)
            ? Directory.GetFiles(cacheDirectory, "manifest.json", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (cachedManifest is null)
        {
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                var downloadUrl = $"https://github.com/gorhill/uBlock/releases/download/{version}/uBlock0_{version}.chromium.zip";
                var zipPath = Path.Combine(Path.GetTempPath(), $"ublock-{version}-{Guid.NewGuid():N}.zip");
                logger.Information("Downloading uBlock Origin {Version}.", version);

                using var httpClient = new HttpClient();
                await using (var stream = await httpClient.GetStreamAsync(downloadUrl, cancellationToken))
                await using (var file = File.Create(zipPath))
                {
                    await stream.CopyToAsync(file, cancellationToken);
                }

                ZipFile.ExtractToDirectory(zipPath, cacheDirectory, true);
                TryDeleteFile(zipPath);
                cachedManifest = Directory.GetFiles(cacheDirectory, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch (Exception ex)
            {
                logger.Warning("Could not download uBlock Origin: {Message}", ex.Message);
            }
        }

        if (cachedManifest is not null)
        {
            var sourceDirectory = Path.GetDirectoryName(cachedManifest)!;
            CopyDirectory(sourceDirectory, extensionDirectory);
            logger.Information("uBlock Origin source copied from cache.");
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            File.Copy(file, Path.Combine(targetDirectory, relative), true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private async Task RunProcessAsync(string executable, string arguments, string workingDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        var cleanExecutable = NormalizeCommandExecutable(executable);
        var scriptPath = Path.Combine(workingDirectory, $".woo-build-{Guid.NewGuid():N}.cmd");
        var command = $"{CreateCmdInvocation(cleanExecutable)} {arguments}";
        await File.WriteAllTextAsync(
            scriptPath,
            $"""
            @echo off
            chcp 65001 >nul
            {command}
            exit /b %ERRORLEVEL%
            """,
            new UTF8Encoding(false),
            cancellationToken);

        logger.Information("> chcp 65001");
        logger.Information("> {Command:l}", command);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                logger.Information(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                if (IsProcessWarningLine(args.Data))
                {
                    logger.Warning(args.Data);
                }
                else if (IsProcessErrorLine(args.Data))
                {
                    logger.Error(args.Data);
                }
                else
                {
                    logger.Information(args.Data);
                }
            }
        };

        if (!process.Start())
        {
            TryDeleteFile(scriptPath);
            throw new InvalidOperationException($"Could not start {executable}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDeleteFile(scriptPath);
            throw;
        }

        if (process.ExitCode != 0)
        {
            TryDeleteFile(scriptPath);
            throw new InvalidOperationException("Build command failed.");
        }

        TryDeleteFile(scriptPath);
    }

    private static bool IsProcessWarningLine(string line)
    {
        return line.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("warning", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessErrorLine(string line)
    {
        var normalized = AnsiColorRegex.Replace(line, string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized.StartsWith("error", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("fatal", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("panic", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.Contains(" build failed", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" command failed", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("could not ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("exception", StringComparison.OrdinalIgnoreCase);
    }

    private string GetNpmExecutable()
    {
        return string.IsNullOrWhiteSpace(_settingsService.Settings.NpmPath)
            ? "npm"
            : _settingsService.Settings.NpmPath;
    }

    private static string NormalizeCommandExecutable(string executable)
    {
        var normalized = executable.Trim();
        while (normalized.StartsWith("\\\"", StringComparison.Ordinal) &&
               normalized.EndsWith("\\\"", StringComparison.Ordinal) &&
               normalized.Length >= 4)
        {
            normalized = normalized[2..^2].Trim();
        }

        return normalized.Trim('"').Trim();
    }

    private static string CreateCmdInvocation(string executable)
    {
        var token = NeedsCmdQuotes(executable) ? QuoteForCmd(executable) : executable;
        var extension = Path.GetExtension(executable);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("npm.cmd", StringComparison.OrdinalIgnoreCase)
            ? $"call {token}"
            : token;
    }

    private static bool NeedsCmdQuotes(string value)
    {
        return value.Any(char.IsWhiteSpace) ||
               value.Contains(Path.DirectorySeparatorChar) ||
               value.Contains(Path.AltDirectorySeparatorChar) ||
               value.Contains(':');
    }

    private static string QuoteForCmd(string value)
    {
        return $"\"{value.Replace("\"", string.Empty)}\"";
    }

    private async Task SaveHistoryAsync(BuildConfiguration configuration, string projectDirectory, string status, string logText, TimeSpan duration)
    {
        var record = new ExportRecord
        {
            AppName = configuration.AppName,
            Url = GetHistorySource(configuration),
            Framework = configuration.Framework.ToString(),
            OutputPath = projectDirectory,
            IconPath = CreateHistoryIconCopy(projectDirectory, configuration.ResolvedIconPath),
            IconUrl = configuration.IconUrl,
            AdBlockerEnabled = configuration.IncludeAdBlocker,
            SingleExe = configuration.SingleExecutable,
            NewLinkRedirect = configuration.NewLinkRedirect,
            AllowDownloads = configuration.AllowDownloads,
            WindowWidth = configuration.WindowWidth,
            WindowHeight = configuration.WindowHeight,
            BuildDurationSeconds = (int)Math.Max(0, duration.TotalSeconds),
            CreatedAt = DateTime.Now,
            Status = status,
            BuildLog = logText
        };

        await _databaseService.AddExportAsync(record);
    }

    private static string GetHistorySource(BuildConfiguration configuration)
    {
        return configuration.SourceKind == AppSourceKind.Website
            ? configuration.WebsiteUrl
            : configuration.LocalSourcePath;
    }

    private async Task RunPostBuildActionsAsync(BuildConfiguration configuration, string projectDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        var executable = FindBuiltExecutable(configuration, projectDirectory);

        if (configuration.CreateDesktopShortcutAfterBuild && !string.IsNullOrWhiteSpace(executable))
        {
            TryCreateDesktopShortcut(configuration.AppName, executable, logger);
        }

        if (configuration.ShowBuildCompleteNotification)
        {
            TryShowBuildCompleteNotification(configuration.AppName);
        }

        if (configuration.OpenFolderAfterBuild && Directory.Exists(projectDirectory))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = projectDirectory,
                UseShellExecute = true
            });
        }

        if (configuration.OpenAppAfterBuild && !string.IsNullOrWhiteSpace(executable))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = true
            });
        }

        await Task.CompletedTask.WaitAsync(cancellationToken);
    }

    private static string? FindBuiltExecutable(BuildConfiguration configuration, string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
        {
            return null;
        }

        var appName = StringSanitizer.ForFileName(configuration.AppName);
        var candidates = Directory.GetFiles(projectDirectory, "*.exe", SearchOption.AllDirectories)
            .Where(path =>
            {
                var file = Path.GetFileName(path);
                return !file.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                       !file.Contains("installer", StringComparison.OrdinalIgnoreCase) &&
                       !file.Equals("electron.exe", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(path => Path.GetFileNameWithoutExtension(path).Contains(appName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Length)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static void TryCreateDesktopShortcut(string appName, string executable, ILogger logger)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcutPath = Path.Combine(desktop, $"{StringSanitizer.ForFileName(appName)}.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = executable;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
            shortcut.IconLocation = executable;
            shortcut.Save();
            logger.Information("Desktop shortcut created.");
        }
        catch (Exception ex)
        {
            logger.Warning("Could not create desktop shortcut: {Message}", ex.Message);
        }
    }

    private static void TryShowBuildCompleteNotification(string appName)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("Woo! build finished")
                .AddText(appName)
                .AddArgument("action", "build-complete")
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
        }
    }

    private static string? GetFaviconUrl(string websiteUrl)
    {
        return UrlHelper.TryNormalize(websiteUrl, out var uri, out _)
            ? $"https://{uri.Host}/favicon.ico"
            : null;
    }

    private static string? CreateHistoryIconCopy(string projectDirectory, string? resolvedIconPath)
    {
        var pngPath = Path.Combine(projectDirectory, "icon.png");
        var source = File.Exists(pngPath) ? pngPath : resolvedIconPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            return source;
        }

        try
        {
            var iconsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Woo!", "history-icons");
            Directory.CreateDirectory(iconsDirectory);
            var extension = Path.GetExtension(source);
            var target = Path.Combine(iconsDirectory, $"{Guid.NewGuid():N}{extension}");
            File.Copy(source, target, true);
            return target;
        }
        catch
        {
            return source;
        }
    }

    private static BuildResult CreateResult(bool success, bool canceled, string message, string projectDirectory, StringBuilder logBuilder)
    {
        return new BuildResult
        {
            Success = success,
            Canceled = canceled,
            Message = message,
            ProjectDirectory = projectDirectory,
            LogText = logBuilder.ToString()
        };
    }

    private static BuildResult CreateResultWithFinalStatus(
        bool success,
        bool canceled,
        string message,
        string projectDirectory,
        StringBuilder logBuilder,
        IProgress<BuildLogEntry> progress)
    {
        var statusText = success
            ? "Build succeeded."
            : canceled
                ? "Build canceled."
                : "Build failed.";
        var severity = success
            ? "Green"
            : canceled
                ? "Yellow"
                : "Red";
        var line = $"[{DateTime.Now:HH:mm:ss}] {statusText}";

        logBuilder.AppendLine(line);
        progress.Report(new BuildLogEntry(line, severity));

        return CreateResult(success, canceled, message, projectDirectory, logBuilder);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }

    private sealed class ProgressLogSink : ILogEventSink
    {
        private readonly IProgress<BuildLogEntry> _progress;
        private readonly StringBuilder _builder;

        public ProgressLogSink(IProgress<BuildLogEntry> progress, StringBuilder builder)
        {
            _progress = progress;
            _builder = builder;
        }

        public void Emit(LogEvent logEvent)
        {
            var (message, consoleColor) = StripAnsi(logEvent.RenderMessage());
            if (consoleColor == "Normal")
            {
                consoleColor = logEvent.Level switch
                {
                    LogEventLevel.Fatal or LogEventLevel.Error => "Red",
                    LogEventLevel.Warning => "Yellow",
                    LogEventLevel.Debug or LogEventLevel.Verbose => "DarkGray",
                    _ => "White"
                };
            }

            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            _builder.AppendLine(line);
            _progress.Report(new BuildLogEntry(line, consoleColor));
        }

        private static (string Message, string ConsoleColor) StripAnsi(string message)
        {
            var color = "Normal";
            foreach (Match match in AnsiColorRegex.Matches(message))
            {
                var codes = match.Groups["codes"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var code in codes)
                {
                    color = code switch
                    {
                        "0" or "39" => "Normal",
                        "30" => "Black",
                        "31" => "DarkRed",
                        "32" => "DarkGreen",
                        "33" => "DarkYellow",
                        "34" => "DarkBlue",
                        "35" => "DarkMagenta",
                        "36" => "DarkCyan",
                        "37" => "Gray",
                        "90" => "DarkGray",
                        "91" => "Red",
                        "92" => "Green",
                        "93" => "Yellow",
                        "94" => "Blue",
                        "95" => "Magenta",
                        "96" => "Cyan",
                        "97" => "White",
                        _ => color
                    };
                }
            }

            return (AnsiColorRegex.Replace(message, string.Empty), color);
        }
    }
}
