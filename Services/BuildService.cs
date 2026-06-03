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

            if (configuration.CustomScriptsEnabled)
            {
                logger.Information("[Woo] Custom Scripts enabled");
                logger.Information("[Woo] Validating WooScript...");
                var scriptValidation = WooScriptService.Validate(configuration.CustomScriptCode, configuration.Framework);
                foreach (var warning in scriptValidation.Warnings)
                {
                    logger.Warning("[WooScript Warning] {Warning}", warning);
                }

                if (!scriptValidation.Success)
                {
                    var message = scriptValidation.Errors.FirstOrDefault() ?? "WooScript validation failed.";
                    logger.Error(message);
                    return CreateResultWithFinalStatus(false, false, message, projectDirectory, logBuilder, progress);
                }

                logger.Information("[Woo] WooScript validation passed");
            }

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
                var tauriBuildCommand = configuration.IncludeInstaller
                    ? "run tauri -- build"
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
        if (configuration.IncludeInstaller)
        {
            configuration.SingleExecutable = false;
        }

        if (configuration.Framework == OutputFramework.Tauri)
        {
            configuration.IncludeAdBlocker = false;
            configuration.SingleExecutable = false;
            configuration.CustomScriptsEnabled = false;
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
                    : configuration.IncludeInstaller
                        ? "electron-builder --win nsis"
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

        if (configuration.CustomScriptsEnabled)
        {
            logger.Information("[Woo] Adding WooScript runtime...");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "wooscript-runtime.js"), CreateElectronWooScriptRuntime(), cancellationToken);
            logger.Information("[Woo] Writing custom script...");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "wooscript.woo"), configuration.CustomScriptCode, cancellationToken);
            logger.Information("[Woo] Custom Scripts ready");
        }

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
        var customScriptsEnabled = configuration.CustomScriptsEnabled ? "true" : "false";
        var wooScriptEmbeddedSource = JsonSerializer.Serialize(configuration.CustomScriptsEnabled ? configuration.CustomScriptCode : string.Empty);
        var persistentPartition = JsonSerializer.Serialize($"persist:woo-{StringSanitizer.ForPackageName(configuration.AppName)}");
        var appUserModelId = JsonSerializer.Serialize($"com.woo.{StringSanitizer.ForIdentifierPart(configuration.AppName)}");

        return $$"""
            const { app, BrowserWindow, session, Tray, Menu, shell, clipboard, Notification, dialog, nativeImage } = require('electron');
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
            const customScriptsEnabled = {{customScriptsEnabled}};
            const wooScriptEmbeddedSource = {{wooScriptEmbeddedSource}};
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

            function installWooScriptRuntime(win, appSession) {
              if (!customScriptsEnabled) {
                return null;
              }

              try {
                const scriptPath = path.join(__dirname, 'wooscript.woo');
                const runtimePath = path.join(__dirname, 'wooscript-runtime.js');
                if (!fs.existsSync(runtimePath)) {
                  console.warn('[WooScript] Runtime file is missing.');
                  return null;
                }

                const { createWooScriptRuntime } = require(runtimePath);
                const source = fs.existsSync(scriptPath)
                  ? fs.readFileSync(scriptPath, 'utf8')
                  : wooScriptEmbeddedSource;
                if (!String(source || '').trim()) {
                  console.warn('[WooScript] Script source is empty.');
                  return null;
                }

                const runtime = createWooScriptRuntime({
                  app,
                  BrowserWindow,
                  win,
                  session: appSession,
                  shell,
                  clipboard,
                  Notification,
                  dialog,
                  nativeImage,
                  fs,
                  path,
                  configuredTitle,
                  targetUrl
                });
                runtime.start(source);
                console.log('[WooScript] Runtime started.');
                return runtime;
              } catch (error) {
                console.warn(`[WooScript] Could not start runtime: ${error.message}`);
                return null;
              }
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

                if (!customScriptsEnabled) {
                  try {
                    item.setSavePath(path.join(app.getPath('downloads'), item.getFilename()));
                  } catch {
                  }
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
                installWooScriptRuntime(child, appSession);
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
              installWooScriptRuntime(win, appSession);

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

    private static string CreateElectronWooScriptRuntime()
    {
        return """""
            'use strict';

            const zlib = require('zlib');

            function createWooScriptRuntime(context) {
              const {
                app,
                BrowserWindow,
                win,
                session,
                shell,
                clipboard,
                Notification,
                dialog,
                nativeImage,
                fs,
                path,
                configuredTitle,
                targetUrl
              } = context;

              const variables = new Map();
              const shortcuts = [];
              const insertedCssKeys = [];
              let downloadsAllowed = null;
              let downloadsFolder = null;
              let downloadsAskWhereToSave = false;
              let navigationCancelNext = false;
              let navigationLockedToMain = false;
              const navigationRules = {
                allow: [],
                block: [],
                redirect: [],
                external: []
              };
              const downloadEventBlocks = {
                start: [],
                finish: [],
                error: []
              };

              function log(message) {
                console.log(`[WooScript] ${message}`);
              }

              function warn(message) {
                console.warn(`[WooScript] ${message}`);
              }

              function sleep(ms) {
                return new Promise((resolve) => setTimeout(resolve, ms));
              }

              function parseDuration(value) {
                const text = String(value || '').trim().toLowerCase();
                const match = text.match(/^(\d+)(ms|s|m)?$/);
                if (!match) {
                  return 0;
                }

                const amount = Number(match[1]);
                const unit = match[2] || 'ms';
                if (unit === 'm') {
                  return amount * 60000;
                }

                if (unit === 's') {
                  return amount * 1000;
                }

                return amount;
              }

              function createBadgeImageSvg(kind, value) {
                const textValue = String(value || '').slice(0, 3);
                const colorMap = {
                  green: '#16c60c',
                  red: '#e74856',
                  yellow: '#f9d165',
                  orange: '#f7630c',
                  blue: '#3b78ff',
                  purple: '#b4009e',
                  white: '#f2f2f2',
                  dot: '#3b78ff',
                  loading: '#3b78ff',
                  sync: '#3b78ff',
                  recording: '#e74856',
                  muted: '#767676',
                  live: '#e74856',
                  error: '#e74856',
                  warning: '#f9d165',
                  info: '#3b78ff',
                  lock: '#767676',
                  unlock: '#16c60c',
                  star: '#f9d165',
                  fire: '#f7630c',
                  time: '#3b78ff',
                  download: '#16c60c',
                  upload: '#3b78ff',
                  update: '#3b78ff',
                  battery: '#16c60c',
                  playmode: '#16c60c',
                  pausemode: '#f9d165',
                  alertmode: '#f9d165',
                  successmode: '#16c60c',
                  gamemode: '#767676',
                  dnd: '#767676'
                };
                const symbolMap = {
                  loading: '...',
                  sync: 'S',
                  recording: 'REC',
                  muted: 'M',
                  live: 'LIVE',
                  error: 'X',
                  warning: '!',
                  info: 'i',
                  lock: 'L',
                  unlock: 'U',
                  star: '*',
                  fire: 'F',
                  time: 'T',
                  download: 'D',
                  upload: 'U',
                  update: 'UP',
                  battery: 'B',
                  playmode: '>',
                  pausemode: 'II',
                  alertmode: '!',
                  successmode: 'OK',
                  gamemode: 'G',
                  dnd: 'DND'
                };
                const color = colorMap[String(kind || '').toLowerCase()] || colorMap.blue;
                const label = kind === 'text'
                  ? textValue
                  : symbolMap[String(kind || '').toLowerCase()] || '';
                const fontSize = label.length > 2 ? 8 : 13;
                return `<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">
                  <circle cx="16" cy="16" r="13" fill="${color}" stroke="rgba(255,255,255,.9)" stroke-width="2"/>
                  ${label ? `<text x="16" y="20" text-anchor="middle" fill="#fff" font-family="Segoe UI, Arial" font-size="${fontSize}" font-weight="700">${label.replace(/[<>&]/g, '')}</text>` : ''}
                </svg>`;
              }

              const pngCrcTable = (() => {
                const table = new Uint32Array(256);
                for (let i = 0; i < 256; i += 1) {
                  let c = i;
                  for (let k = 0; k < 8; k += 1) {
                    c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
                  }
                  table[i] = c >>> 0;
                }
                return table;
              })();

              function crc32(buffer) {
                let c = 0xffffffff;
                for (const byte of buffer) {
                  c = pngCrcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
                }
                return (c ^ 0xffffffff) >>> 0;
              }

              function pngChunk(type, data) {
                const typeBuffer = Buffer.from(type, 'ascii');
                const length = Buffer.alloc(4);
                length.writeUInt32BE(data.length, 0);
                const crc = Buffer.alloc(4);
                crc.writeUInt32BE(crc32(Buffer.concat([typeBuffer, data])), 0);
                return Buffer.concat([length, typeBuffer, data, crc]);
              }

              function createBadgePngBuffer(kind, value) {
                const width = 32;
                const height = 32;
                const rgba = Buffer.alloc(width * height * 4);
                const colorMap = {
                  green: [22, 198, 12],
                  red: [231, 72, 86],
                  yellow: [249, 209, 101],
                  orange: [247, 99, 12],
                  blue: [59, 120, 255],
                  purple: [180, 0, 158],
                  white: [242, 242, 242],
                  dot: [59, 120, 255],
                  loading: [59, 120, 255],
                  sync: [59, 120, 255],
                  recording: [231, 72, 86],
                  muted: [118, 118, 118],
                  live: [231, 72, 86],
                  error: [231, 72, 86],
                  warning: [249, 209, 101],
                  info: [59, 120, 255],
                  lock: [118, 118, 118],
                  unlock: [22, 198, 12],
                  star: [249, 209, 101],
                  fire: [247, 99, 12],
                  time: [59, 120, 255],
                  download: [22, 198, 12],
                  upload: [59, 120, 255],
                  update: [59, 120, 255],
                  battery: [22, 198, 12],
                  playmode: [22, 198, 12],
                  pausemode: [249, 209, 101],
                  alertmode: [249, 209, 101],
                  successmode: [22, 198, 12],
                  gamemode: [118, 118, 118],
                  dnd: [118, 118, 118]
                };
                const glyphMap = {
                  loading: '...',
                  sync: 'S',
                  recording: 'REC',
                  muted: 'M',
                  live: 'LIVE',
                  error: 'X',
                  warning: '!',
                  info: 'I',
                  lock: 'L',
                  unlock: 'U',
                  star: '*',
                  fire: 'F',
                  time: 'T',
                  download: 'D',
                  upload: 'U',
                  update: 'UP',
                  battery: 'B',
                  playmode: '>',
                  pausemode: 'II',
                  alertmode: '!',
                  successmode: 'OK',
                  gamemode: 'G',
                  dnd: 'DND'
                };
                const font = {
                  '0': ['111', '101', '101', '101', '111'], '1': ['010', '110', '010', '010', '111'],
                  '2': ['111', '001', '111', '100', '111'], '3': ['111', '001', '111', '001', '111'],
                  '4': ['101', '101', '111', '001', '001'], '5': ['111', '100', '111', '001', '111'],
                  '6': ['111', '100', '111', '101', '111'], '7': ['111', '001', '010', '010', '010'],
                  '8': ['111', '101', '111', '101', '111'], '9': ['111', '101', '111', '001', '111'],
                  'A': ['010', '101', '111', '101', '101'], 'B': ['110', '101', '110', '101', '110'],
                  'D': ['110', '101', '101', '101', '110'], 'E': ['111', '100', '110', '100', '111'],
                  'F': ['111', '100', '110', '100', '100'], 'G': ['111', '100', '101', '101', '111'],
                  'I': ['111', '010', '010', '010', '111'], 'K': ['101', '101', '110', '101', '101'],
                  'L': ['100', '100', '100', '100', '111'], 'M': ['101', '111', '111', '101', '101'],
                  'N': ['101', '111', '111', '111', '101'], 'O': ['111', '101', '101', '101', '111'],
                  'P': ['111', '101', '111', '100', '100'], 'R': ['110', '101', '110', '101', '101'],
                  'S': ['111', '100', '111', '001', '111'], 'T': ['111', '010', '010', '010', '010'],
                  'U': ['101', '101', '101', '101', '111'], 'V': ['101', '101', '101', '101', '010'],
                  'W': ['101', '101', '111', '111', '101'], 'X': ['101', '101', '010', '101', '101'],
                  'Y': ['101', '101', '010', '010', '010'], '!': ['010', '010', '010', '000', '010'],
                  '*': ['101', '010', '111', '010', '101'], '>': ['100', '010', '001', '010', '100'],
                  '+': ['000', '010', '111', '010', '000'], '.': ['000', '000', '000', '000', '010']
                };
                const normalizedKind = String(kind || '').toLowerCase();
                const color = colorMap[normalizedKind] || colorMap.blue;
                const label = normalizedKind === 'text'
                  ? String(value || '').toUpperCase().slice(0, 3)
                  : (glyphMap[normalizedKind] || '').toUpperCase().slice(0, 4);

                function setPixel(x, y, colorValue, alpha = 255) {
                  if (x < 0 || y < 0 || x >= width || y >= height) {
                    return;
                  }
                  const offset = (y * width + x) * 4;
                  rgba[offset] = colorValue[0];
                  rgba[offset + 1] = colorValue[1];
                  rgba[offset + 2] = colorValue[2];
                  rgba[offset + 3] = alpha;
                }

                for (let y = 0; y < height; y += 1) {
                  for (let x = 0; x < width; x += 1) {
                    const dx = x - 16;
                    const dy = y - 16;
                    const distance = Math.sqrt(dx * dx + dy * dy);
                    if (distance <= 14) {
                      setPixel(x, y, color, 255);
                    } else if (distance <= 15.5) {
                      setPixel(x, y, [255, 255, 255], 220);
                    }
                  }
                }

                function drawGlyph(char, startX, startY, scale) {
                  const rows = font[char] || font['!'];
                  for (let row = 0; row < rows.length; row += 1) {
                    for (let col = 0; col < rows[row].length; col += 1) {
                      if (rows[row][col] !== '1') {
                        continue;
                      }
                      for (let yy = 0; yy < scale; yy += 1) {
                        for (let xx = 0; xx < scale; xx += 1) {
                          setPixel(startX + col * scale + xx, startY + row * scale + yy, [255, 255, 255], 255);
                        }
                      }
                    }
                  }
                }

                if (label) {
                  const scale = label.length <= 2 ? 4 : 3;
                  const glyphWidth = 3 * scale;
                  const gap = Math.max(1, Math.floor(scale / 2));
                  const totalWidth = label.length * glyphWidth + (label.length - 1) * gap;
                  let x = Math.floor((width - totalWidth) / 2);
                  const y = Math.floor((height - 5 * scale) / 2) + 1;
                  for (const char of label) {
                    drawGlyph(char, x, y, scale);
                    x += glyphWidth + gap;
                  }
                }

                const raw = Buffer.alloc((width * 4 + 1) * height);
                for (let y = 0; y < height; y += 1) {
                  raw[y * (width * 4 + 1)] = 0;
                  rgba.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
                }

                const ihdr = Buffer.alloc(13);
                ihdr.writeUInt32BE(width, 0);
                ihdr.writeUInt32BE(height, 4);
                ihdr[8] = 8;
                ihdr[9] = 6;
                ihdr[10] = 0;
                ihdr[11] = 0;
                ihdr[12] = 0;

                return Buffer.concat([
                  Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
                  pngChunk('IHDR', ihdr),
                  pngChunk('IDAT', zlib.deflateSync(raw)),
                  pngChunk('IEND', Buffer.alloc(0))
                ]);
              }

              function setBadgeOverlay(kind, value) {
                if (!win.setOverlayIcon || !nativeImage || !nativeImage.createFromBuffer) {
                  return false;
                }

                try {
                  const image = nativeImage.createFromBuffer(createBadgePngBuffer(kind, value));
                  if (image && image.isEmpty && image.isEmpty() && nativeImage.createFromDataURL) {
                    const svg = createBadgeImageSvg(kind, value);
                    const fallback = nativeImage.createFromDataURL(`data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`);
                    win.setOverlayIcon(fallback, `Woo badge ${kind || ''}`.trim());
                    return true;
                  }

                  win.setOverlayIcon(image, `Woo badge ${kind || ''}`.trim());
                  return true;
                } catch (error) {
                  warn(`Could not set taskbar badge: ${error.message}`);
                  return false;
                }
              }

              function normalizeBadgeKind(value) {
                const text = String(value || '').trim().toLowerCase();
                const aliases = {
                  play: 'playmode',
                  pause: 'pausemode',
                  alert: 'alertmode',
                  success: 'successmode',
                  game: 'gamemode',
                  online: 'green',
                  busy: 'red',
                  away: 'yellow'
                };

                return aliases[text] || text;
              }

              function clearBadgeOverlay() {
                if (win.setOverlayIcon) {
                  win.setOverlayIcon(null, '');
                }
              }

              function setBadgeValue(value) {
                const raw = String(value || '').trim();
                const numberMatch = raw.match(/^num\s+(.+)$/i);
                if (numberMatch) {
                  value = numberMatch[1].trim();
                }

                if (typeof value === 'number' || /^\d+$/.test(String(value || ''))) {
                  if (app.setBadgeCount) {
                    app.setBadgeCount(Number(value) || 0);
                  }
                  setBadgeOverlay('text', String(value || '0'));
                  return;
                }

                const text = normalizeBadgeKind(value);
                if (!text || text === 'clear' || text === 'none' || text === 'off') {
                  if (app.setBadgeCount) {
                    app.setBadgeCount(0);
                  }
                  clearBadgeOverlay();
                  return;
                }

                if (text === '99plus' || text === '99+') {
                  if (app.setBadgeCount) {
                    app.setBadgeCount(99);
                  }
                  setBadgeOverlay('text', '99+');
                  return;
                }

                setBadgeOverlay(text === 'dot' ? 'dot' : text, text);
              }

              function stripComments(line) {
                let inString = false;
                let inTriple = false;
                for (let i = 0; i < line.length; i += 1) {
                  if (!inString && line.startsWith('"""', i)) {
                    inTriple = !inTriple;
                    i += 2;
                    continue;
                  }

                  const current = line[i];
                  if (!inTriple && current === '"' && line[i - 1] !== '\\') {
                    inString = !inString;
                    continue;
                  }

                  if (inString || inTriple) {
                    continue;
                  }

                  const isBoundary = i === 0 || /\s/.test(line[i - 1]);
                  if (current === '#' && isBoundary) {
                    return line.slice(0, i);
                  }

                  if (current === '/' && line[i + 1] === '/' && isBoundary) {
                    return line.slice(0, i);
                  }

                  if (current === ':' && line[i + 1] === ':' && isBoundary) {
                    return line.slice(0, i);
                  }
                }

                return line;
              }

              function countToken(value, token) {
                let count = 0;
                let index = 0;
                while ((index = value.indexOf(token, index)) !== -1) {
                  count += 1;
                  index += token.length;
                }

                return count;
              }

              function splitLogicalLines(source) {
                const rawLines = String(source || '').replace(/\r\n/g, '\n').split('\n');
                const lines = [];
                let buffer = '';
                let inTriple = false;

                for (const rawLine of rawLines) {
                  if (inTriple) {
                    buffer += `\n${rawLine}`;
                    if (countToken(rawLine, '"""') % 2 === 1) {
                      lines.push(buffer);
                      buffer = '';
                      inTriple = false;
                    }

                    continue;
                  }

                  if (countToken(rawLine, '"""') % 2 === 1) {
                    buffer = rawLine;
                    inTriple = true;
                    continue;
                  }

                  lines.push(rawLine);
                }

                if (buffer) {
                  lines.push(buffer);
                }

                return lines;
              }

              function cleanLine(line) {
                return stripComments(line).trim();
              }

              function parse(source) {
                const logicalLines = splitLogicalLines(source);

                function parseBlock(startIndex) {
                  const statements = [];
                  let index = startIndex;
                  while (index < logicalLines.length) {
                    const line = cleanLine(logicalLines[index]);
                    if (!line) {
                      index += 1;
                      continue;
                    }

                    if (line === '}') {
                      return { statements, index: index + 1, closedByElse: false };
                    }

                    if (line.startsWith('} else')) {
                      return { statements, index, closedByElse: true };
                    }

                    const inlineEmptyHeader = line.match(/^(on\s+.+|if\s+.+|every\s+\S+|after\s+\S+|shortcut\s+"[^"]+")\s*\{\s*\}$/i);
                    if (inlineEmptyHeader) {
                      const headerText = inlineEmptyHeader[1].trim();
                      if (headerText.startsWith('on ')) {
                        statements.push({ type: 'on', event: headerText.slice(3).trim(), body: [] });
                      } else if (headerText.startsWith('if ')) {
                        statements.push({ type: 'if', condition: headerText.slice(3).trim(), body: [], elseBody: [] });
                      } else if (headerText.startsWith('every ')) {
                        statements.push({ type: 'every', duration: headerText.slice(6).trim(), body: [] });
                      } else if (headerText.startsWith('after ')) {
                        statements.push({ type: 'after', duration: headerText.slice(6).trim(), body: [] });
                      } else if (headerText.startsWith('shortcut ')) {
                        const match = headerText.match(/^shortcut\s+"([^"]+)"/i);
                        statements.push({ type: 'shortcut', accelerator: match ? match[1] : '', body: [] });
                      }

                      index += 1;
                      continue;
                    }

                    const header = line.match(/^(on\s+.+|if\s+.+|every\s+\S+|after\s+\S+|shortcut\s+"[^"]+")\s*\{$/i);
                    if (header) {
                      const headerText = header[1].trim();
                      const parsedBody = parseBlock(index + 1);
                      let elseBody = [];
                      index = parsedBody.index;

                      if (parsedBody.closedByElse) {
                        const elseLine = cleanLine(logicalLines[index]);
                        if (/^\}\s*else\s*\{$/i.test(elseLine) || /^else\s*\{$/i.test(elseLine)) {
                          const parsedElse = parseBlock(index + 1);
                          elseBody = parsedElse.statements;
                          index = parsedElse.index;
                        }
                      }

                      if (headerText.startsWith('on ')) {
                        statements.push({ type: 'on', event: headerText.slice(3).trim(), body: parsedBody.statements });
                      } else if (headerText.startsWith('if ')) {
                        statements.push({ type: 'if', condition: headerText.slice(3).trim(), body: parsedBody.statements, elseBody });
                      } else if (headerText.startsWith('every ')) {
                        statements.push({ type: 'every', duration: headerText.slice(6).trim(), body: parsedBody.statements });
                      } else if (headerText.startsWith('after ')) {
                        statements.push({ type: 'after', duration: headerText.slice(6).trim(), body: parsedBody.statements });
                      } else if (headerText.startsWith('shortcut ')) {
                        const match = headerText.match(/^shortcut\s+"([^"]+)"/i);
                        statements.push({ type: 'shortcut', accelerator: match ? match[1] : '', body: parsedBody.statements });
                      }

                      continue;
                    }

                    statements.push({ type: 'command', line, lineNumber: index + 1 });
                    index += 1;
                  }

                  return { statements, index, closedByElse: false };
                }

                return parseBlock(0).statements;
              }

              function splitArgs(value) {
                const args = [];
                let current = '';
                let inString = false;
                let inTriple = false;
                let depth = 0;

                for (let i = 0; i < value.length; i += 1) {
                  if (!inString && value.startsWith('"""', i)) {
                    inTriple = !inTriple;
                    current += '"""';
                    i += 2;
                    continue;
                  }

                  const char = value[i];
                  if (!inTriple && char === '"' && value[i - 1] !== '\\') {
                    inString = !inString;
                    current += char;
                    continue;
                  }

                  if (!inString && !inTriple) {
                    if (char === '(') {
                      depth += 1;
                    } else if (char === ')') {
                      depth = Math.max(0, depth - 1);
                    } else if (char === ',' && depth === 0) {
                      args.push(current.trim());
                      current = '';
                      continue;
                    }
                  }

                  current += char;
                }

                if (current.trim().length > 0) {
                  args.push(current.trim());
                }

                return args.map(evalValue);
              }

              function splitConcat(value) {
                const parts = [];
                let current = '';
                let inString = false;
                let inTriple = false;

                for (let i = 0; i < value.length; i += 1) {
                  if (!inString && value.startsWith('"""', i)) {
                    inTriple = !inTriple;
                    current += '"""';
                    i += 2;
                    continue;
                  }

                  const char = value[i];
                  if (!inTriple && char === '"' && value[i - 1] !== '\\') {
                    inString = !inString;
                    current += char;
                    continue;
                  }

                  if (!inString && !inTriple && char === '+') {
                    parts.push(current.trim());
                    current = '';
                    continue;
                  }

                  current += char;
                }

                if (current.trim()) {
                  parts.push(current.trim());
                }

                return parts;
              }

              function unquote(value) {
                const text = String(value || '').trim();
                if (text.startsWith('"""') && text.endsWith('"""')) {
                  return text.slice(3, -3);
                }

                if (text.startsWith('"') && text.endsWith('"')) {
                  return text.slice(1, -1)
                    .replace(/\\"/g, '"')
                    .replace(/\\n/g, '\n')
                    .replace(/\\t/g, '\t')
                    .replace(/\\\\/g, '\\');
                }

                return text;
              }

              function evalValue(raw) {
                const value = String(raw || '').trim();
                if (!value) {
                  return '';
                }

                const concatParts = splitConcat(value);
                if (concatParts.length > 1) {
                  return concatParts.map(evalValue).join('');
                }

                if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith('"""') && value.endsWith('"""'))) {
                  return unquote(value);
                }

                if (/^\d+(\.\d+)?$/.test(value)) {
                  return Number(value);
                }

                if (/^(true|false)$/i.test(value)) {
                  return /^true$/i.test(value);
                }

                if (value === 'page.url' || value === 'url') {
                  return win.webContents.getURL();
                }

                if (value === 'page.title' || value === 'title') {
                  return win.webContents.getTitle();
                }

                if (value === 'app.name') {
                  return configuredTitle;
                }

                if (value === 'app.version') {
                  return app.getVersion ? app.getVersion() : '1.0.0';
                }

                if (value === 'app.platform') {
                  return process.platform === 'win32' ? 'windows' : process.platform;
                }

                if (variables.has(value)) {
                  return variables.get(value);
                }

                return value;
              }

              function parseCall(line) {
                const match = line.match(/^([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s*\(([\s\S]*)\)\s*$/);
                if (!match) {
                  return null;
                }

                return {
                  name: match[1],
                  args: splitArgs(match[2])
                };
              }

              async function executeJavaScript(source) {
                return win.webContents.executeJavaScript(String(source || ''), true);
              }

              async function runDomScript(functionSource, args = []) {
                const source = `(() => {
                  const args = ${JSON.stringify(args)};
                  return (${functionSource})(...args);
                })()`;
                return executeJavaScript(source);
              }

              async function resolvePageIconUrl() {
                try {
                  const pageIcon = await runDomScript(() => {
                    const selectors = [
                      'link[rel~="icon"][href]',
                      'link[rel="shortcut icon"][href]',
                      'link[rel="apple-touch-icon"][href]',
                      'link[rel="mask-icon"][href]'
                    ];
                    for (const selector of selectors) {
                      const node = document.querySelector(selector);
                      if (node && node.href) {
                        return node.href;
                      }
                    }

                    return '';
                  });
                  if (pageIcon) {
                    return String(pageIcon);
                  }

                  const currentUrl = win.webContents.getURL();
                  if (!/^https?:\/\//i.test(currentUrl)) {
                    return '';
                  }

                  const url = new URL(currentUrl);
                  return `${url.origin}/favicon.ico`;
                } catch {
                  return '';
                }
              }

              async function setBadgeFromSiteIcon() {
                if (!win.setOverlayIcon || !nativeImage || !nativeImage.createFromBuffer || typeof fetch !== 'function') {
                  return false;
                }

                const iconUrl = await resolvePageIconUrl();
                if (!iconUrl) {
                  return false;
                }

                try {
                  const response = await fetch(iconUrl);
                  if (!response.ok) {
                    return false;
                  }

                  const bytes = Buffer.from(await response.arrayBuffer());
                  if (bytes.length === 0) {
                    return false;
                  }

                  const image = nativeImage.createFromBuffer(bytes);
                  if (image && image.isEmpty && image.isEmpty()) {
                    return false;
                  }

                  win.setOverlayIcon(image, 'Website icon');
                  return true;
                } catch {
                  return false;
                }
              }

              async function selectorExists(selector) {
                try {
                  return !!await runDomScript((sel) => !!document.querySelector(sel), [selector]);
                } catch {
                  return false;
                }
              }

              async function selectorText(selector) {
                try {
                  return await runDomScript((sel) => document.querySelector(sel)?.textContent || '', [selector]);
                } catch {
                  return '';
                }
              }

              function wildcardToRegex(pattern) {
                const escaped = String(pattern)
                  .replace(/[.+^${}()|[\]\\]/g, '\\$&')
                  .replace(/\*/g, '.*');
                return new RegExp(`^${escaped}$`, 'i');
              }

              async function evaluateCondition(condition) {
                const text = condition.trim();
                const normalized = text
                  .replace(/^page\.url\b/i, 'url')
                  .replace(/^page\.title\b/i, 'title');

                let match = normalized.match(/^(url|title)\s+contains\s+"([\s\S]*)"$/i);
                if (match) {
                  const value = match[1].toLowerCase() === 'url' ? win.webContents.getURL() : win.webContents.getTitle();
                  return value.toLowerCase().includes(match[2].toLowerCase());
                }

                match = normalized.match(/^(url|title)\s+startsWith\s+"([\s\S]*)"$/i);
                if (match) {
                  const value = match[1].toLowerCase() === 'url' ? win.webContents.getURL() : win.webContents.getTitle();
                  return value.toLowerCase().startsWith(match[2].toLowerCase());
                }

                match = normalized.match(/^(url|title)\s+endsWith\s+"([\s\S]*)"$/i);
                if (match) {
                  const value = match[1].toLowerCase() === 'url' ? win.webContents.getURL() : win.webContents.getTitle();
                  return value.toLowerCase().endsWith(match[2].toLowerCase());
                }

                match = normalized.match(/^(url|title)\s+matches\s+"([\s\S]*)"$/i);
                if (match) {
                  const value = match[1].toLowerCase() === 'url' ? win.webContents.getURL() : win.webContents.getTitle();
                  return wildcardToRegex(match[2]).test(value);
                }

                match = normalized.match(/^(url|title)\s*(==|=|!=)\s*"([\s\S]*)"$/i);
                if (match) {
                  const value = match[1].toLowerCase() === 'url' ? win.webContents.getURL() : win.webContents.getTitle();
                  return match[2] === '!=' ? value !== match[3] : value === match[3];
                }

                match = text.match(/^selector\.exists\("([\s\S]*)"\)$/i);
                if (match) {
                  return selectorExists(match[1]);
                }

                match = text.match(/^selector\.text\("([\s\S]*)"\)\s+contains\s+"([\s\S]*)"$/i);
                if (match) {
                  const value = await selectorText(match[1]);
                  return value.toLowerCase().includes(match[2].toLowerCase());
                }

                if (/^window\.isMaximized$/i.test(text)) {
                  return win.isMaximized();
                }

                if (/^window\.isFullscreen$/i.test(text)) {
                  return win.isFullScreen();
                }

                match = text.match(/^app\.platform\s*(==|=|!=)\s*"([\s\S]*)"$/i);
                if (match) {
                  const platform = process.platform === 'win32' ? 'windows' : process.platform;
                  return match[1] === '!=' ? platform !== match[2].toLowerCase() : platform === match[2].toLowerCase();
                }

                warn(`Unsupported condition: ${text}`);
                return false;
              }

              async function executeCommand(line) {
                try {
                  if (line.startsWith('wait ')) {
                    await sleep(parseDuration(line.slice(5).trim()));
                    return;
                  }

                  if (line.startsWith('let ')) {
                    const match = line.match(/^let\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([\s\S]+)$/);
                    if (match) {
                      variables.set(match[1], evalValue(match[2]));
                    }
                    return;
                  }

                  const call = parseCall(line);
                  if (!call) {
                    warn(`Unknown command: ${line}`);
                    return;
                  }

                  const [a, b, c] = call.args;
                  switch (call.name) {
                    case 'app.quit':
                      app.quit();
                      break;
                    case 'app.restart':
                      if (app.relaunch) {
                        app.relaunch();
                        app.exit(0);
                      }
                      break;
                    case 'app.showMessage':
                    case 'alert':
                      await dialog.showMessageBox(win, { type: 'info', message: String(a || '') });
                      break;
                    case 'app.log':
                      log(String(a || ''));
                      break;
                    case 'app.openExternal':
                      await shell.openExternal(String(a || ''));
                      break;
                    case 'app.setBadge':
                    case 'badge.set':
                      setBadgeValue(a);
                      break;
                    case 'app.setBadgeCount':
                    case 'badge.count':
                      if (String(a || '').trim().toLowerCase() === '99plus' || String(a || '').trim() === '99+') {
                        if (app.setBadgeCount) {
                          app.setBadgeCount(99);
                        }
                        setBadgeOverlay('text', '99+');
                      } else {
                        if (app.setBadgeCount) {
                          app.setBadgeCount(Number(a) || 0);
                        }
                        setBadgeOverlay('text', String(Number(a) || 0));
                      }
                      break;
                    case 'app.setBadgeDot':
                    case 'badge.dot':
                      setBadgeOverlay(String(a || 'dot'), '');
                      break;
                    case 'app.setBadgeText':
                    case 'badge.text':
                      setBadgeOverlay('text', String(a || ''));
                      break;
                    case 'app.setBadgeStatus':
                    case 'badge.status':
                      setBadgeOverlay(String(a || 'info'), '');
                      break;
                    case 'app.setBadgeIcon':
                    case 'badge.icon':
                      if (win.setOverlayIcon && nativeImage && nativeImage.createFromPath) {
                        const image = nativeImage.createFromPath(String(a || ''));
                        win.setOverlayIcon(image, 'Woo badge icon');
                      }
                      break;
                    case 'app.setBadgeFromSiteIcon':
                    case 'badge.siteIcon':
                      await setBadgeFromSiteIcon();
                      break;
                    case 'app.clearBadge':
                    case 'badge.clear':
                      if (app.setBadgeCount) {
                        app.setBadgeCount(0);
                      }
                      clearBadgeOverlay();
                      break;
                    case 'window.setTitle':
                      win.setTitle(String(a || configuredTitle));
                      break;
                    case 'window.resize':
                      win.setSize(Number(a) || 1280, Number(b) || 800);
                      break;
                    case 'window.setWidth': {
                      const size = win.getSize();
                      win.setSize(Number(a) || size[0], size[1]);
                      break;
                    }
                    case 'window.setHeight': {
                      const size = win.getSize();
                      win.setSize(size[0], Number(a) || size[1]);
                      break;
                    }
                    case 'window.center':
                      win.center();
                      break;
                    case 'window.maximize':
                      win.maximize();
                      break;
                    case 'window.unmaximize':
                      win.unmaximize();
                      break;
                    case 'window.minimize':
                      win.minimize();
                      break;
                    case 'window.restore':
                      win.restore();
                      break;
                    case 'window.fullscreen':
                      win.setFullScreen(Boolean(a));
                      break;
                    case 'window.toggleFullscreen':
                      win.setFullScreen(!win.isFullScreen());
                      break;
                    case 'window.alwaysOnTop':
                      win.setAlwaysOnTop(Boolean(a));
                      break;
                    case 'window.setResizable':
                      win.setResizable(Boolean(a));
                      break;
                    case 'window.show':
                      win.show();
                      break;
                    case 'window.hide':
                      win.hide();
                      break;
                    case 'window.focus':
                      win.focus();
                      break;
                    case 'window.blur':
                      if (win.blur) {
                        win.blur();
                      }
                      break;
                    case 'window.flash':
                      win.flashFrame(true);
                      break;
                    case 'window.setOpacity':
                      win.setOpacity(Math.max(0.1, Math.min(1, Number(a) || 1)));
                      break;
                    case 'devtools.open':
                      win.webContents.openDevTools({ mode: 'detach' });
                      break;
                    case 'devtools.close':
                      win.webContents.closeDevTools();
                      break;
                    case 'devtools.toggle':
                      if (win.webContents.isDevToolsOpened()) {
                        win.webContents.closeDevTools();
                      } else {
                        win.webContents.openDevTools({ mode: 'detach' });
                      }
                      break;
                    case 'page.reload':
                      win.webContents.reload();
                      break;
                    case 'page.reloadIgnoringCache':
                      win.webContents.reloadIgnoringCache();
                      break;
                    case 'page.back':
                      if (win.webContents.canGoBack()) {
                        win.webContents.goBack();
                      }
                      break;
                    case 'page.forward':
                      if (win.webContents.canGoForward()) {
                        win.webContents.goForward();
                      }
                      break;
                    case 'page.stop':
                      win.webContents.stop();
                      break;
                    case 'page.load':
                      await win.loadURL(String(a || targetUrl));
                      break;
                    case 'page.setZoom':
                      win.webContents.setZoomFactor(Number(a) || 1);
                      break;
                    case 'page.getZoom':
                      log(String(win.webContents.getZoomFactor()));
                      break;
                    case 'page.zoomIn':
                      win.webContents.setZoomFactor(win.webContents.getZoomFactor() + 0.1);
                      break;
                    case 'page.zoomOut':
                      win.webContents.setZoomFactor(Math.max(0.25, win.webContents.getZoomFactor() - 0.1));
                      break;
                    case 'page.resetZoom':
                      win.webContents.setZoomFactor(1);
                      break;
                    case 'page.print':
                      win.webContents.print();
                      break;
                    case 'page.saveAsPdf': {
                      const pdfBuffer = await win.webContents.printToPDF({});
                      const outputPath = String(a || path.join(app.getPath('documents'), `${configuredTitle || 'Woo'}.pdf`));
                      await fs.promises.mkdir(path.dirname(outputPath), { recursive: true });
                      await fs.promises.writeFile(outputPath, pdfBuffer);
                      log(`PDF saved: ${outputPath}`);
                      break;
                    }
                    case 'page.screenshot': {
                      const image = await win.webContents.capturePage();
                      const outputPath = String(a || path.join(app.getPath('pictures'), `${configuredTitle || 'Woo'}.png`));
                      await fs.promises.mkdir(path.dirname(outputPath), { recursive: true });
                      await fs.promises.writeFile(outputPath, image.toPNG());
                      log(`Screenshot saved: ${outputPath}`);
                      break;
                    }
                    case 'page.find':
                      win.webContents.findInPage(String(a || ''));
                      break;
                    case 'page.clearFind':
                      win.webContents.stopFindInPage('clearSelection');
                      break;
                    case 'js.run':
                    case 'runjs':
                    case 'inject':
                      await executeJavaScript(String(a || ''));
                      break;
                    case 'js.eval':
                      await executeJavaScript(String(a || ''));
                      break;
                    case 'js.file': {
                      const source = await fs.promises.readFile(String(a || ''), 'utf8');
                      await executeJavaScript(source);
                      break;
                    }
                    case 'css.inject': {
                      const key = await win.webContents.insertCSS(String(a || ''), { cssOrigin: 'user' });
                      insertedCssKeys.push(key);
                      break;
                    }
                    case 'css.file': {
                      const css = await fs.promises.readFile(String(a || ''), 'utf8');
                      const key = await win.webContents.insertCSS(css, { cssOrigin: 'user' });
                      insertedCssKeys.push(key);
                      break;
                    }
                    case 'css.removeAll':
                      while (insertedCssKeys.length > 0) {
                        const key = insertedCssKeys.pop();
                        await win.webContents.removeInsertedCSS(key).catch(() => {});
                      }
                      break;
                    case 'css.theme': {
                      const mode = String(a || '').toLowerCase();
                      const css = mode === 'dark'
                        ? 'html, body { color-scheme: dark !important; }'
                        : mode === 'light'
                          ? 'html, body { color-scheme: light !important; }'
                          : String(a || '');
                      const key = await win.webContents.insertCSS(css, { cssOrigin: 'user' });
                      insertedCssKeys.push(key);
                      break;
                    }
                    case 'css.hide':
                    case 'hide': {
                      const selector = String(a || '');
                      const key = await win.webContents.insertCSS(`${selector} { display: none !important; visibility: hidden !important; }`, { cssOrigin: 'user' });
                      insertedCssKeys.push(key);
                      break;
                    }
                    case 'css.show': {
                      const selector = String(a || '');
                      const key = await win.webContents.insertCSS(`${selector} { display: revert !important; visibility: visible !important; }`, { cssOrigin: 'user' });
                      insertedCssKeys.push(key);
                      break;
                    }
                    case 'page.click':
                    case 'click':
                      await runDomScript((sel) => document.querySelector(sel)?.click(), [String(a || '')]);
                      break;
                    case 'page.clickAll':
                      await runDomScript((sel) => document.querySelectorAll(sel).forEach((node) => node.click()), [String(a || '')]);
                      break;
                    case 'page.type':
                    case 'type':
                    case 'page.setValue':
                      await runDomScript((sel, value) => {
                        const node = document.querySelector(sel);
                        if (!node) return false;
                        node.focus();
                        node.value = value;
                        node.dispatchEvent(new Event('input', { bubbles: true }));
                        node.dispatchEvent(new Event('change', { bubbles: true }));
                        return true;
                      }, [String(a || ''), String(b || '')]);
                      break;
                    case 'page.clear':
                      await runDomScript((sel) => {
                        const node = document.querySelector(sel);
                        if (!node) return false;
                        node.value = '';
                        node.dispatchEvent(new Event('input', { bubbles: true }));
                        return true;
                      }, [String(a || '')]);
                      break;
                    case 'page.focus':
                      await runDomScript((sel) => document.querySelector(sel)?.focus(), [String(a || '')]);
                      break;
                    case 'page.blur':
                      await runDomScript((sel) => document.querySelector(sel)?.blur(), [String(a || '')]);
                      break;
                    case 'page.text':
                    case 'queryText':
                      log(await selectorText(String(a || '')));
                      break;
                    case 'page.html':
                    case 'queryHtml':
                      log(await runDomScript((sel) => document.querySelector(sel)?.innerHTML || '', [String(a || '')]));
                      break;
                    case 'page.attr':
                      log(await runDomScript((sel, attr) => document.querySelector(sel)?.getAttribute(attr) || '', [String(a || ''), String(b || '')]));
                      break;
                    case 'page.setAttr':
                      await runDomScript((sel, attr, value) => document.querySelector(sel)?.setAttribute(attr, value), [String(a || ''), String(b || ''), String(c || '')]);
                      break;
                    case 'page.exists':
                    case 'query':
                      log(String(await selectorExists(String(a || ''))));
                      break;
                    case 'queryAll':
                      log(await runDomScript((sel) => String(document.querySelectorAll(sel).length), [String(a || '')]));
                      break;
                    case 'page.waitFor':
                    case 'waitFor': {
                      const selector = String(a || '');
                      const timeout = Number(b) || 10000;
                      const started = Date.now();
                      while (Date.now() - started < timeout) {
                        if (await selectorExists(selector)) {
                          break;
                        }
                        await sleep(100);
                      }
                      break;
                    }
                    case 'page.remove':
                    case 'remove':
                      await runDomScript((sel) => document.querySelectorAll(sel).forEach((node) => node.remove()), [String(a || '')]);
                      break;
                    case 'page.scrollTo':
                      await runDomScript((x, y) => window.scrollTo(Number(x) || 0, Number(y) || 0), [a, b]);
                      break;
                    case 'page.scrollTop':
                      await runDomScript(() => window.scrollTo(0, 0));
                      break;
                    case 'page.scrollBottom':
                      await runDomScript(() => window.scrollTo(0, document.body.scrollHeight));
                      break;
                    case 'page.addClass':
                      await runDomScript((sel, name) => document.querySelector(sel)?.classList.add(name), [String(a || ''), String(b || '')]);
                      break;
                    case 'page.removeClass':
                      await runDomScript((sel, name) => document.querySelector(sel)?.classList.remove(name), [String(a || ''), String(b || '')]);
                      break;
                    case 'page.toggleClass':
                      await runDomScript((sel, name) => document.querySelector(sel)?.classList.toggle(name), [String(a || ''), String(b || '')]);
                      break;
                    case 'setText':
                      await runDomScript((sel, value) => { const node = document.querySelector(sel); if (node) node.textContent = value; }, [String(a || ''), String(b || '')]);
                      break;
                    case 'setHtml':
                      await runDomScript((sel, value) => { const node = document.querySelector(sel); if (node) node.innerHTML = value; }, [String(a || ''), String(b || '')]);
                      break;
                    case 'setStyle':
                      await runDomScript((sel, prop, value) => { const node = document.querySelector(sel); if (node) node.style[prop] = value; }, [String(a || ''), String(b || ''), String(c || '')]);
                      break;
                    case 'notify': {
                      const title = call.args.length > 1 ? String(a || configuredTitle) : configuredTitle;
                      const body = call.args.length > 1 ? String(b || '') : String(a || '');
                      if (Notification && Notification.isSupported && Notification.isSupported()) {
                        new Notification({ title, body }).show();
                      } else {
                        await dialog.showMessageBox(win, { type: 'info', title, message: body || title });
                      }
                      break;
                    }
                    case 'dialog.info':
                    case 'dialog.warning':
                    case 'dialog.error':
                      await dialog.showMessageBox(win, {
                        type: call.name.split('.')[1] === 'error' ? 'error' : call.name.split('.')[1],
                        title: String(a || configuredTitle),
                        message: String(b || a || '')
                      });
                      break;
                    case 'dialog.confirm': {
                      const result = await dialog.showMessageBox(win, {
                        type: 'question',
                        title: String(a || configuredTitle),
                        message: String(b || a || ''),
                        buttons: ['OK', 'Cancel'],
                        defaultId: 0,
                        cancelId: 1
                      });
                      log(result.response === 0 ? 'true' : 'false');
                      break;
                    }
                    case 'toast': {
                      const title = call.args.length > 1 ? String(a || configuredTitle) : configuredTitle;
                      const body = call.args.length > 1 ? String(b || '') : String(a || '');
                      if (Notification && Notification.isSupported && Notification.isSupported()) {
                        new Notification({ title, body }).show();
                      } else {
                        log(body || title);
                      }
                      break;
                    }
                    case 'clipboard.writeText':
                      clipboard.writeText(String(a || ''));
                      break;
                    case 'clipboard.readText':
                      log(clipboard.readText ? clipboard.readText() : '');
                      break;
                    case 'clipboard.clear':
                      clipboard.writeText('');
                      break;
                    case 'storage.local.set':
                      await runDomScript((key, value) => localStorage.setItem(key, value), [String(a || ''), String(b || '')]);
                      break;
                    case 'storage.local.get':
                      log(await runDomScript((key) => localStorage.getItem(key) || '', [String(a || '')]));
                      break;
                    case 'storage.local.remove':
                      await runDomScript((key) => localStorage.removeItem(key), [String(a || '')]);
                      break;
                    case 'storage.local.clear':
                      await runDomScript(() => localStorage.clear());
                      break;
                    case 'storage.session.set':
                      await runDomScript((key, value) => sessionStorage.setItem(key, value), [String(a || ''), String(b || '')]);
                      break;
                    case 'storage.session.get':
                      log(await runDomScript((key) => sessionStorage.getItem(key) || '', [String(a || '')]));
                      break;
                    case 'storage.session.remove':
                      await runDomScript((key) => sessionStorage.removeItem(key), [String(a || '')]);
                      break;
                    case 'storage.session.clear':
                      await runDomScript(() => sessionStorage.clear());
                      break;
                    case 'cookies.set':
                      await runDomScript((name, value) => { document.cookie = `${name}=${encodeURIComponent(value)}; path=/`; }, [String(a || ''), String(b || '')]);
                      break;
                    case 'cookies.get':
                      log(await runDomScript((name) => {
                        const prefix = `${name}=`;
                        return document.cookie.split(';').map((part) => part.trim()).find((part) => part.startsWith(prefix))?.slice(prefix.length) || '';
                      }, [String(a || '')]));
                      break;
                    case 'cookies.remove':
                      await runDomScript((name) => { document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/`; }, [String(a || '')]);
                      break;
                    case 'cookies.clear':
                      await session.clearStorageData({ storages: ['cookies'] });
                      break;
                    case 'cache.clear':
                      await session.clearCache();
                      break;
                    case 'userAgent.set':
                      session.setUserAgent(String(a || ''));
                      break;
                    case 'userAgent.reset':
                      session.setUserAgent('');
                      break;
                    case 'downloads.allow':
                      downloadsAllowed = true;
                      break;
                    case 'downloads.block':
                      downloadsAllowed = false;
                      break;
                    case 'downloads.setFolder':
                      downloadsFolder = String(a || '');
                      break;
                    case 'downloads.askWhereToSave':
                      downloadsAskWhereToSave = call.args.length === 0 ? true : Boolean(a);
                      break;
                    case 'navigation.cancel':
                      navigationCancelNext = true;
                      break;
                    case 'navigation.lockToMain':
                      navigationLockedToMain = true;
                      break;
                    case 'navigation.unlock':
                      navigationLockedToMain = false;
                      break;
                    case 'navigation.block':
                      navigationRules.block.push(String(a || ''));
                      break;
                    case 'navigation.allow':
                      navigationRules.allow.push(String(a || ''));
                      break;
                    case 'navigation.redirect':
                      navigationRules.redirect.push({ from: String(a || ''), to: String(b || '') });
                      break;
                    case 'navigation.openExternal':
                      navigationRules.external.push(String(a || ''));
                      break;
                    case 'navigation.setNewLinks':
                      setNewLinkPolicy(a || 'app');
                      break;
                    default:
                      warn(`Unsupported command: ${call.name}`);
                      break;
                  }
                } catch (error) {
                  warn(`Command failed: ${line} (${error.message})`);
                }
              }

              async function executeStatements(statements, trigger = {}) {
                for (const statement of statements) {
                  if (statement.type === 'command') {
                    await executeCommand(statement.line);
                  } else if (statement.type === 'if') {
                    const matched = await evaluateCondition(statement.condition);
                    await executeStatements(matched ? statement.body : statement.elseBody || [], trigger);
                  }
                }
              }

              function matchesPattern(value, pattern) {
                if (wildcardToRegex(pattern).test(value)) {
                  return true;
                }

                if (String(pattern || '').includes('*')) {
                  return false;
                }

                try {
                  const current = new URL(value);
                  const expected = new URL(pattern);
                  const currentHost = current.hostname.replace(/^www\./i, '').toLowerCase();
                  const expectedHost = expected.hostname.replace(/^www\./i, '').toLowerCase();
                  const expectedPath = expected.pathname === '/' ? '/' : expected.pathname.replace(/\/+$/g, '');
                  const currentPath = current.pathname === '/' ? '/' : current.pathname.replace(/\/+$/g, '');
                  return current.protocol === expected.protocol &&
                    currentHost === expectedHost &&
                    (expectedPath === '/' || currentPath === expectedPath || currentPath.startsWith(`${expectedPath}/`));
                } catch {
                  return String(value || '').toLowerCase().includes(String(pattern || '').toLowerCase());
                }
              }

              function isSameMainUrl(value) {
                try {
                  const current = new URL(value);
                  const target = new URL(targetUrl);
                  current.hash = '';
                  target.hash = '';
                  return current.href === target.href;
                } catch {
                  return false;
                }
              }

              function installNavigationRuntime() {
                win.webContents.on('will-navigate', (event, url) => {
                  if (navigationCancelNext) {
                    navigationCancelNext = false;
                    event.preventDefault();
                    return;
                  }

                  if (navigationLockedToMain && !isSameMainUrl(url)) {
                    event.preventDefault();
                    return;
                  }

                  for (const pattern of navigationRules.external) {
                    if (matchesPattern(url, pattern)) {
                      event.preventDefault();
                      shell.openExternal(url).catch((error) => warn(error.message));
                      return;
                    }
                  }

                  for (const rule of navigationRules.redirect) {
                    if (matchesPattern(url, rule.from)) {
                      event.preventDefault();
                      win.loadURL(rule.to).catch((error) => warn(error.message));
                      return;
                    }
                  }

                  if (navigationRules.block.some((pattern) => matchesPattern(url, pattern)) &&
                      !navigationRules.allow.some((pattern) => matchesPattern(url, pattern))) {
                    event.preventDefault();
                  }
                });

                session.on('will-download', (event, item) => {
                  if (downloadsAllowed === false) {
                    event.preventDefault();
                    warn(`Download blocked: ${item.getURL ? item.getURL() : item.getFilename()}`);
                    return;
                  }

                  if (!downloadsAskWhereToSave && downloadsFolder && item.setSavePath) {
                    try {
                      item.setSavePath(path.join(downloadsFolder, item.getFilename()));
                    } catch (error) {
                      warn(`Could not set download path: ${error.message}`);
                    }
                  }

                  runDownloadEvent('start', item);
                  if (item && item.once) {
                    item.once('done', (_event, state) => {
                      runDownloadEvent(state === 'completed' ? 'finish' : 'error', item);
                    });
                  }
                });
              }

              function runDownloadEvent(name, item) {
                const blocks = downloadEventBlocks[name] || [];
                for (const body of blocks) {
                  executeStatements(body, { event: `download.${name}`, download: item })
                    .catch((error) => warn(error.message));
                }
              }

              function setNewLinkPolicy(mode) {
                const normalized = String(mode || '').toLowerCase();
                win.webContents.setWindowOpenHandler(({ url }) => {
                  if (navigationLockedToMain && !isSameMainUrl(url)) {
                    return { action: 'deny' };
                  }

                  if (normalized === 'block' || normalized === 'deny') {
                    return { action: 'deny' };
                  }

                  if (normalized === 'browser' || normalized === 'external') {
                    shell.openExternal(url).catch((error) => warn(error.message));
                    return { action: 'deny' };
                  }

                  if (normalized === 'new' && BrowserWindow) {
                    const child = new BrowserWindow({
                      width: 1100,
                      height: 780,
                      title: configuredTitle,
                      webPreferences: {
                        contextIsolation: true,
                        nodeIntegration: false,
                        sandbox: true,
                        session
                      }
                    });
                    child.loadURL(url).catch((error) => warn(error.message));
                    return { action: 'deny' };
                  }

                  win.loadURL(url).catch((error) => warn(error.message));
                  return { action: 'deny' };
                });
              }

              function normalizeAccelerator(input) {
                return String(input || '')
                  .split('+')
                  .map((part) => part.trim().toLowerCase())
                  .filter(Boolean)
                  .sort()
                  .join('+');
              }

              function inputToAccelerator(input) {
                const parts = [];
                if (input.control) parts.push('ctrl');
                if (input.shift) parts.push('shift');
                if (input.alt) parts.push('alt');
                if (input.meta) parts.push('meta');
                const key = String(input.key || '').toLowerCase();
                if (key === ' ') {
                  parts.push('space');
                } else {
                  parts.push(key);
                }
                return parts.filter(Boolean).sort().join('+');
              }

              function installShortcut(accelerator, body) {
                shortcuts.push({
                  accelerator: normalizeAccelerator(accelerator),
                  body
                });
              }

              function installShortcutListener() {
                win.webContents.on('before-input-event', (event, input) => {
                  const current = inputToAccelerator(input);
                  const shortcut = shortcuts.find((entry) => entry.accelerator === current);
                  if (!shortcut) {
                    return;
                  }

                  event.preventDefault();
                  executeStatements(shortcut.body).catch((error) => warn(error.message));
                });
              }

              function registerEventBlock(block) {
                const event = block.event.trim();
                const run = () => executeStatements(block.body, { event }).catch((error) => warn(error.message));

                if (/^(app\.ready|window\.ready)$/i.test(event)) {
                  setTimeout(run, 0);
                  return;
                }

                if (/^app\.close$/i.test(event)) {
                  app.on('before-quit', run);
                  return;
                }

                if (/^window\.focus$/i.test(event)) {
                  win.on('focus', run);
                  return;
                }

                if (/^window\.blur$/i.test(event)) {
                  win.on('blur', run);
                  return;
                }

                if (/^(page\.ready|page\.loaded)$/i.test(event)) {
                  win.webContents.on('did-finish-load', run);
                  win.webContents.on('dom-ready', run);
                  return;
                }

                if (/^page\.error$/i.test(event)) {
                  win.webContents.on('did-fail-load', run);
                  return;
                }

                if (/^page\.loading$/i.test(event) || /^navigation\.start$/i.test(event)) {
                  win.webContents.on('did-start-navigation', run);
                  return;
                }

                if (/^navigation\.finish$/i.test(event)) {
                  win.webContents.on('did-navigate', run);
                  win.webContents.on('did-navigate-in-page', run);
                  return;
                }

                if (/^page\.titleChanged$/i.test(event)) {
                  win.webContents.on('page-title-updated', run);
                  return;
                }

                if (/^page\.urlChanged$/i.test(event)) {
                  win.webContents.on('did-navigate', run);
                  win.webContents.on('did-navigate-in-page', run);
                  return;
                }

                if (/^download\.start$/i.test(event)) {
                  downloadEventBlocks.start.push(block.body);
                  return;
                }

                if (/^download\.finish$/i.test(event)) {
                  downloadEventBlocks.finish.push(block.body);
                  return;
                }

                if (/^download\.error$/i.test(event)) {
                  downloadEventBlocks.error.push(block.body);
                  return;
                }

                const urlContains = event.match(/^url\.contains\("([\s\S]*)"\)$/i);
                if (urlContains) {
                  const check = () => {
                    if (win.webContents.getURL().toLowerCase().includes(urlContains[1].toLowerCase())) {
                      run();
                    }
                  };
                  win.webContents.on('did-navigate', check);
                  win.webContents.on('did-navigate-in-page', check);
                  win.webContents.on('did-finish-load', check);
                  return;
                }

                const urlMatch = event.match(/^url\.match\("([\s\S]*)"\)$/i);
                if (urlMatch) {
                  const check = () => {
                    if (matchesPattern(win.webContents.getURL(), urlMatch[1])) {
                      run();
                    }
                  };
                  win.webContents.on('did-navigate', check);
                  win.webContents.on('did-navigate-in-page', check);
                  win.webContents.on('did-finish-load', check);
                  return;
                }

                const titleContains = event.match(/^title\.contains\("([\s\S]*)"\)$/i);
                if (titleContains) {
                  const check = () => {
                    if (win.webContents.getTitle().toLowerCase().includes(titleContains[1].toLowerCase())) {
                      run();
                    }
                  };
                  win.webContents.on('page-title-updated', check);
                  win.webContents.on('did-finish-load', check);
                  return;
                }

                const selectorMatch = event.match(/^selector\.exists\("([\s\S]*)"\)$/i);
                if (selectorMatch) {
                  const check = async () => {
                    if (await selectorExists(selectorMatch[1])) {
                      await run();
                    }
                  };
                  win.webContents.on('did-finish-load', check);
                  win.webContents.on('dom-ready', check);
                  return;
                }

                warn(`Unsupported event: ${event}`);
              }

              function registerStatement(statement) {
                if (statement.type === 'on') {
                  registerEventBlock(statement);
                } else if (statement.type === 'every') {
                  const interval = parseDuration(statement.duration);
                  if (interval > 0) {
                    setInterval(() => executeStatements(statement.body).catch((error) => warn(error.message)), interval);
                  }
                } else if (statement.type === 'after') {
                  const delay = parseDuration(statement.duration);
                  setTimeout(() => executeStatements(statement.body).catch((error) => warn(error.message)), delay);
                } else if (statement.type === 'shortcut') {
                  installShortcut(statement.accelerator, statement.body);
                }
              }

              function shouldWaitForPage(statement) {
                if (statement.type !== 'command') {
                  return true;
                }

                if (/^wait\s+/i.test(statement.line)) {
                  return false;
                }

                const call = parseCall(statement.line);
                if (!call) {
                  return true;
                }

                const name = call.name.toLowerCase();
                if (name === 'app.setbadgefromsiteicon' || name === 'badge.siteicon') {
                  return true;
                }

                return name.startsWith('page.') ||
                  name.startsWith('js.') ||
                  name === 'runjs' ||
                  name === 'inject' ||
                  name.startsWith('css.') ||
                  name.startsWith('storage.') ||
                  name.startsWith('cookies.') ||
                  name.startsWith('cache.') ||
                  ['query', 'querytext', 'queryhtml', 'queryall', 'settext', 'sethtml', 'setstyle', 'click', 'type', 'waitfor', 'hide', 'remove'].includes(name);
              }

              function start(source) {
                const statements = parse(source);
                const immediateStatements = [];
                const pageReadyStatements = [];
                installNavigationRuntime();
                installShortcutListener();

                for (const statement of statements) {
                  if (statement.type === 'command' || statement.type === 'if') {
                    if (shouldWaitForPage(statement)) {
                      pageReadyStatements.push(statement);
                    } else {
                      immediateStatements.push(statement);
                    }
                  } else {
                    registerStatement(statement);
                  }
                }

                if (immediateStatements.length > 0) {
                  setTimeout(() => {
                    executeStatements(immediateStatements).catch((error) => warn(error.message));
                  }, 0);
                }

                if (pageReadyStatements.length > 0) {
                  let ranInitialStatements = false;
                  const runInitialStatements = () => {
                    if (ranInitialStatements) {
                      return;
                    }

                    ranInitialStatements = true;
                    executeStatements(pageReadyStatements).catch((error) => warn(error.message));
                  };

                  win.webContents.once('dom-ready', runInitialStatements);
                  win.webContents.once('did-finish-load', runInitialStatements);
                  setTimeout(runInitialStatements, 10000);
                }
              }

              return { start };
            }

            module.exports = { createWooScriptRuntime };
            """"";
    }

    private static string CreateElectronBuilderYaml(BuildConfiguration configuration, string identifier)
    {
        var target = configuration.SingleExecutable
            ? "portable"
            : configuration.IncludeInstaller
                ? "nsis"
                : "dir";
        var portableBlock = configuration.SingleExecutable
            ? """
              portable:
                artifactName: "${productName}.exe"
              """
            : string.Empty;
        var installerBlock = configuration.IncludeInstaller
            ? """
              nsis:
                oneClick: false
                perMachine: false
                allowToChangeInstallationDirectory: true
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
            {{installerBlock}}
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
                ["active"] = configuration.IncludeInstaller,
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
            IncludeInstaller = configuration.IncludeInstaller,
            NewLinkRedirect = configuration.NewLinkRedirect,
            AllowDownloads = configuration.AllowDownloads,
            CustomScriptsEnabled = configuration.CustomScriptsEnabled,
            CustomScriptCode = configuration.CustomScriptCode,
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
