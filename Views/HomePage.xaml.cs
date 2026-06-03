using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Woo_.Helpers;
using Woo_.Models;
using Woo_.Services;
using Woo_.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Woo_.Views;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; } = App.HomeViewModel;
    private bool _miniAutoScroll = true;
    private bool _miniScrollQueued;
    private Paragraph? _miniLogParagraph;
    private int _miniRenderedLogEntries;
    private bool _customScriptEditorInitialized;
    private int _customScriptDiagnosticsVersion;
    private int _customScriptClearClicks;
    private static readonly JsonSerializerOptions EditorJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HomePage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is BuildConfiguration configuration)
        {
            ViewModel.LoadConfiguration(configuration);
            if (_customScriptEditorInitialized)
            {
                _ = SetCustomScriptEditorTextAsync(ViewModel.CustomScriptCode);
            }
        }
    }

    private async void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.OutputDirectory = folder.Path;
        }
    }

    private async void ChooseCustomIcon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".ico");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.SetCustomIcon(file.Path);
        }
    }

    private async void BrowseLocalSource_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add(".html");
        picker.FileTypeFilter.Add(".htm");

        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.LocalSourcePath = file.Path;
            if (string.IsNullOrWhiteSpace(ViewModel.AppName))
            {
                ViewModel.AppName = Path.GetFileNameWithoutExtension(file.Path);
            }
        }
    }

    private async void ImportAppPreset_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".wooapp");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var configuration = await AppPresetService.ImportAsync(file.Path);
            ViewModel.LoadConfiguration(configuration);
            if (ViewModel.CustomScriptsEnabled)
            {
                await InitializeCustomScriptEditorAsync();
                await SetCustomScriptEditorTextAsync(ViewModel.CustomScriptCode);
            }
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Could not import app settings",
                Content = ex.Message,
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
    }

    private async void ResetAppOptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Restart app options?",
            Content = "Woo! will reset the current export form back to the built-in defaults. Your saved Settings and History are not deleted.",
            MinWidth = 520,
            PrimaryButtonText = "Restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.ResetConfigurationToDefaults();
        if (_customScriptEditorInitialized)
        {
            await SetCustomScriptEditorTextAsync(ViewModel.CustomScriptCode);
        }
    }

    private void WebsiteUrlTextBox_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
    }

    private async void WebsiteUrlTextBox_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.WebLink))
        {
            ViewModel.WebsiteUrl = (await e.DataView.GetWebLinkAsync()).ToString();
            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            ViewModel.WebsiteUrl = (await e.DataView.GetTextAsync()).Trim();
        }
    }

    private async void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        await SyncCustomScriptFromEditorAsync();
        var configuration = ViewModel.CreateConfiguration();
        var conflictChoice = OutputConflictChoice.Rename;

        var appName = string.IsNullOrWhiteSpace(configuration.AppName)
            ? string.IsNullOrWhiteSpace(configuration.LocalSourcePath)
                ? "Woo App"
                : Path.GetFileNameWithoutExtension(configuration.LocalSourcePath)
            : configuration.AppName;
        var projectDirectory = Path.Combine(configuration.OutputDirectory, StringSanitizer.ForFileName(appName));
        if (Directory.Exists(projectDirectory) && Directory.EnumerateFileSystemEntries(projectDirectory).Any())
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Output folder already exists",
                Content = "Choose how Woo! should handle the existing folder.",
                MinWidth = 520,
                PrimaryButtonText = "Overwrite",
                SecondaryButtonText = "Rename",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Secondary
            };

            var result = await dialog.ShowAsync();
            conflictChoice = result switch
            {
                ContentDialogResult.Primary => OutputConflictChoice.Overwrite,
                ContentDialogResult.Secondary => OutputConflictChoice.Rename,
                _ => OutputConflictChoice.Cancel
            };
        }

        if (conflictChoice == OutputConflictChoice.Cancel)
        {
            return;
        }

        await ViewModel.BuildAsync(new BuildRequest(configuration, conflictChoice));
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        _miniAutoScroll = true;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.BuildLogEntries.CollectionChanged += BuildLogEntries_CollectionChanged;
        MiniLogScrollViewer.ViewChanged += MiniLogScrollViewer_ViewChanged;
        RenderMiniLogEntries(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        while (_miniRenderedLogEntries < ViewModel.BuildLogEntries.Count)
        {
            AppendMiniLogEntry(ViewModel.BuildLogEntries[_miniRenderedLogEntries]);
        }

        QueueMiniScrollToBottom();

        if (ViewModel.CustomScriptsEnabled)
        {
            ViewModel.ApplyDefaultCustomScriptIfNeeded();
            await InitializeCustomScriptEditorAsync();
            await SetCustomScriptEditorTextAsync(ViewModel.CustomScriptCode);
        }
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.BuildLogEntries.CollectionChanged -= BuildLogEntries_CollectionChanged;
        MiniLogScrollViewer.ViewChanged -= MiniLogScrollViewer_ViewChanged;
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.CustomScriptsEnabled) && ViewModel.CustomScriptsEnabled)
        {
            await InitializeCustomScriptEditorAsync();
            await SetCustomScriptEditorTextAsync(ViewModel.CustomScriptCode);
        }
    }

    private void BuildLogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var shouldAutoScroll = _miniAutoScroll;
        RenderMiniLogEntries(e);
        if (shouldAutoScroll)
        {
            QueueMiniScrollToBottom();
        }
    }

    private void MiniLogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _miniAutoScroll = MiniLogScrollViewer.VerticalOffset >= MiniLogScrollViewer.ScrollableHeight - 8;
    }

    private void RenderMiniLogEntries(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            MiniLogRichTextBlock.Blocks.Clear();
            _miniLogParagraph = null;
            _miniRenderedLogEntries = 0;
            return;
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<BuildLogEntry>())
            {
                AppendMiniLogEntry(item);
            }

            return;
        }

        while (_miniRenderedLogEntries < ViewModel.BuildLogEntries.Count)
        {
            AppendMiniLogEntry(ViewModel.BuildLogEntries[_miniRenderedLogEntries]);
        }
    }

    private void AppendMiniLogEntry(BuildLogEntry entry)
    {
        _miniLogParagraph ??= CreateMiniLogParagraph();
        if (_miniLogParagraph.Inlines.Count > 0)
        {
            _miniLogParagraph.Inlines.Add(new LineBreak());
        }

        _miniLogParagraph.Inlines.Add(new Run
        {
            Text = entry.Text,
            Foreground = GetLogBrush(entry.Severity)
        });
        _miniRenderedLogEntries++;
    }

    private Paragraph CreateMiniLogParagraph()
    {
        var paragraph = new Paragraph();
        MiniLogRichTextBlock.Blocks.Add(paragraph);
        return paragraph;
    }

    private async void QueueMiniScrollToBottom()
    {
        if (_miniScrollQueued)
        {
            return;
        }

        _miniScrollQueued = true;
        try
        {
            foreach (var delay in new[] { 0, 16, 33, 66, 120 })
            {
                if (delay > 0)
                {
                    await Task.Delay(delay);
                }

                if (!_miniAutoScroll)
                {
                    return;
                }

                _ = DispatcherQueue.TryEnqueue(ScrollMiniLogToBottom);
            }
        }
        finally
        {
            _miniScrollQueued = false;
        }
    }

    private void ScrollMiniLogToBottom()
    {
        MiniLogRichTextBlock.UpdateLayout();
        MiniLogBottomAnchor.UpdateLayout();
        MiniLogScrollViewer.UpdateLayout();
        MiniLogScrollViewer.ChangeView(null, MiniLogScrollViewer.ScrollableHeight, null, true);
        MiniLogBottomAnchor.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 1.0
        });
        _miniAutoScroll = true;
    }

    private void ExpandBuildLog_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateToBuildLog(null);
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(ViewModel.OutputFolderPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = ViewModel.OutputFolderPath,
            UseShellExecute = true
        });
    }

    private void UseCurrentUserAgent_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UseCurrentUserAgent();
    }

    private async void CustomScriptDocs_Click(object sender, RoutedEventArgs e)
    {
        var docsDirectory = Path.Combine(App.SettingsService.AppDataDirectory, "docs");
        Directory.CreateDirectory(docsDirectory);
        var docsPath = Path.Combine(docsDirectory, "wooscript-docs.html");
        await File.WriteAllTextAsync(docsPath, CreateDocsHtml(WooScriptService.GetDocsMarkdown()));

        Process.Start(new ProcessStartInfo
        {
            FileName = docsPath,
            UseShellExecute = true
        });
    }

    private async void ClearCustomScript_Click(object sender, RoutedEventArgs e)
    {
        _customScriptClearClicks++;
        ViewModel.CustomScriptCode = string.Empty;
        ViewModel.ClearCustomScriptValidationMessage();
        await SetCustomScriptEditorTextAsync(string.Empty);
        if (_customScriptClearClicks >= 5)
        {
            _customScriptClearClicks = 0;
            await ShowCustomScriptClearEasterEggAsync();
        }
    }

    private async Task InitializeCustomScriptEditorAsync()
    {
        if (_customScriptEditorInitialized)
        {
            return;
        }

        await CustomScriptEditorWebView.EnsureCoreWebView2Async();
        CustomScriptEditorWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        CustomScriptEditorWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        var monacoDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Monaco");
        if (Directory.Exists(monacoDirectory))
        {
            CustomScriptEditorWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "woo-monaco.local",
                monacoDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        CustomScriptEditorWebView.CoreWebView2.WebMessageReceived += (sender, args) =>
        {
            try
            {
                var message = JsonSerializer.Deserialize<EditorMessage>(args.WebMessageAsJson, EditorJsonOptions);
                if (message?.Type == "changed")
                {
                    ViewModel.CustomScriptCode = message.Value ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(ViewModel.CustomScriptCode))
                    {
                        _customScriptClearClicks = 0;
                    }

                    _ = ApplyCustomScriptDiagnosticsAsync();
                }
                else if (message?.Type == "saved")
                {
                    ViewModel.CustomScriptCode = message.Value ?? string.Empty;
                    ViewModel.SetCustomScriptValidationMessage("Script saved locally.", true);
                    _ = ApplyCustomScriptDiagnosticsAsync();
                }
                else if (message?.Type == "ready")
                {
                    _ = ApplyCustomScriptDiagnosticsAsync();
                }
            }
            catch
            {
            }
        };

        CustomScriptEditorWebView.NavigateToString(CreateCustomScriptEditorHtml(ViewModel.CustomScriptCode));
        _customScriptEditorInitialized = true;
        await ApplyCustomScriptDiagnosticsAsync();
    }

    private async Task SyncCustomScriptFromEditorAsync()
    {
        if (!_customScriptEditorInitialized)
        {
            return;
        }

        try
        {
            var rawResult = await CustomScriptEditorWebView.ExecuteScriptAsync("window.wooEditorGetValue && window.wooEditorGetValue()");
            var value = JsonSerializer.Deserialize<string>(rawResult);
            if (value is not null)
            {
                ViewModel.CustomScriptCode = value;
            }
        }
        catch
        {
        }
    }

    private async Task SetCustomScriptEditorTextAsync(string value)
    {
        if (!_customScriptEditorInitialized)
        {
            if (ViewModel.CustomScriptsEnabled)
            {
                await InitializeCustomScriptEditorAsync();
            }

            return;
        }

        try
        {
            var encoded = JsonSerializer.Serialize(value);
            await CustomScriptEditorWebView.ExecuteScriptAsync($"window.wooEditorSetValue && window.wooEditorSetValue({encoded})");
            await ApplyCustomScriptDiagnosticsAsync();
        }
        catch
        {
        }
    }

    private async Task ApplyCustomScriptDiagnosticsAsync()
    {
        if (!_customScriptEditorInitialized)
        {
            return;
        }

        var version = Interlocked.Increment(ref _customScriptDiagnosticsVersion);
        try
        {
            await Task.Delay(100);
            if (version != _customScriptDiagnosticsVersion)
            {
                return;
            }

            var framework = ViewModel.IsElectronSelected ? OutputFramework.Electron : OutputFramework.Tauri;
            var result = WooScriptService.Validate(ViewModel.CustomScriptCode, framework);
            ViewModel.SetCustomScriptValidationResult(result);
            var diagnostics = result.Diagnostics.Select(diagnostic => new
            {
                line = diagnostic.Line,
                message = diagnostic.Message,
                severity = diagnostic.IsWarning ? "warning" : "error"
            });
            var json = JsonSerializer.Serialize(diagnostics);
            await CustomScriptEditorWebView.ExecuteScriptAsync($"window.wooEditorSetDiagnostics && window.wooEditorSetDiagnostics({json})");
        }
        catch
        {
        }
    }

    private async Task ShowCustomScriptClearEasterEggAsync()
    {
        if (!_customScriptEditorInitialized)
        {
            return;
        }

        try
        {
            await CustomScriptEditorWebView.ExecuteScriptAsync("window.wooEditorCelebrateClear && window.wooEditorCelebrateClear()");
        }
        catch
        {
        }
    }

    private static string CreateDocsHtml(string markdown)
    {
        var body = RenderDocsMarkdown(markdown);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>WooScript Docs</title>
              <style>
                :root {
                  color-scheme: dark;
                  --bg: #0f1115;
                  --panel: #171a21;
                  --panel-2: #101319;
                  --text: #edf1f7;
                  --muted: #aab2c0;
                  --border: #303541;
                  --accent: #7cc4ff;
                  --keyword: #569cd6;
                  --object: #4ec9b0;
                  --string: #ce9178;
                  --number: #b5cea8;
                  --comment: #6a9955;
                  --function: #dcdcaa;
                }

                * { box-sizing: border-box; }
                body {
                  margin: 0;
                  background: var(--bg);
                  color: var(--text);
                  font-family: "Segoe UI", Arial, sans-serif;
                  line-height: 1.6;
                }

                main {
                  width: min(1120px, calc(100vw - 32px));
                  margin: 0 auto;
                  padding: 32px 0 64px;
                }

                h1 {
                  font-size: 34px;
                  margin: 0 0 18px;
                  letter-spacing: 0;
                }

                h2 {
                  margin: 34px 0 12px;
                  font-size: 22px;
                  border-top: 1px solid var(--border);
                  padding-top: 22px;
                }

                p {
                  color: var(--muted);
                  margin: 8px 0 12px;
                }

                pre {
                  margin: 12px 0 18px;
                  padding: 16px;
                  background: var(--panel-2);
                  border: 1px solid var(--border);
                  border-radius: 8px;
                  overflow: auto;
                  white-space: pre;
                }

                code {
                  font-family: "Cascadia Code", Consolas, monospace;
                  font-size: 13px;
                }

                .kw { color: var(--keyword); }
                .obj { color: var(--object); }
                .str { color: var(--string); }
                .num { color: var(--number); }
                .cm { color: var(--comment); }
                .fn { color: var(--function); }
              </style>
            </head>
            <body>
              <main>
                {{body}}
              </main>
            </body>
            </html>
            """;
    }

    private static string RenderDocsMarkdown(string markdown)
    {
        var builder = new System.Text.StringBuilder();
        var codeBuilder = new System.Text.StringBuilder();
        var inCode = false;

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    builder.Append("<pre><code>");
                    builder.Append(HighlightWooScript(AnnotateDocsCode(codeBuilder.ToString().TrimEnd())));
                    builder.AppendLine("</code></pre>");
                    codeBuilder.Clear();
                    inCode = false;
                }
                else
                {
                    inCode = true;
                }

                continue;
            }

            if (inCode)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                codeBuilder.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                builder.Append("<h1>");
                builder.Append(WebUtility.HtmlEncode(line[2..]));
                builder.AppendLine("</h1>");
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                builder.Append("<h2>");
                builder.Append(WebUtility.HtmlEncode(line[3..]));
                builder.AppendLine("</h2>");
            }
            else
            {
                builder.Append("<p>");
                builder.Append(WebUtility.HtmlEncode(line));
                builder.AppendLine("</p>");
            }
        }

        return builder.ToString();
    }

    private static string HighlightWooScript(string code)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in code.Replace("\r\n", "\n").Split('\n'))
        {
            var commentIndex = FindCommentIndex(line);
            var codePart = commentIndex >= 0 ? line[..commentIndex] : line;
            var commentPart = commentIndex >= 0 ? line[commentIndex..] : string.Empty;

            builder.Append(HighlightWooScriptCodePart(codePart));
            if (!string.IsNullOrEmpty(commentPart))
            {
                builder.Append("<span class=\"cm\">");
                builder.Append(WebUtility.HtmlEncode(commentPart));
                builder.Append("</span>");
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string HighlightWooScriptCodePart(string code)
    {
        var strings = new List<string>();
        var encoded = System.Text.RegularExpressions.Regex.Replace(
            WebUtility.HtmlEncode(code),
            @"(&quot;[^&]*(?:\\.[^&]*)?&quot;)",
            match =>
            {
                var key = $"@@WOOSTRING{new string('A', strings.Count + 1)}@@";
                strings.Add($"<span class=\"str\">{match.Value}</span>");
                return key;
            });

        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"\b(on|if|else|let|true|false|wait|every|after|shortcut|contains|startsWith|endsWith|matches)\b", "<span class=\"kw\">$1</span>");
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"\b(app|badge|window|page|js|css|navigation|downloads|dialog|clipboard|storage|cookies|cache|devtools|userAgent|selector|title|url)\b", "<span class=\"obj\">$1</span>");
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"\b\d+(ms|s|m)?\b", "<span class=\"num\">$0</span>");
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"\b([A-Za-z_][A-Za-z0-9_]*)(?=\()", "<span class=\"fn\">$1</span>");

        for (var i = 0; i < strings.Count; i++)
        {
            encoded = encoded.Replace($"@@WOOSTRING{new string('A', i + 1)}@@", strings[i], StringComparison.Ordinal);
        }

        return encoded;
    }

    private static string AnnotateDocsCode(string code)
    {
        var builder = new System.Text.StringBuilder();
        var inTriple = false;
        foreach (var rawLine in code.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();
            var tripleCount = CountOccurrences(line, "\"\"\"");

            if (!inTriple &&
                !line.Contains("\"\"\"", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(trimmed) &&
                !HasInlineComment(trimmed))
            {
                var description = GetDocsCommandDescription(trimmed);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    line += $" # {description}";
                }
            }

            builder.AppendLine(line);
            if (tripleCount % 2 == 1)
            {
                inTriple = !inTriple;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static bool HasInlineComment(string line)
    {
        return FindCommentIndex(line) >= 0;
    }

    private static int FindCommentIndex(string line)
    {
        var inString = false;
        var inTriple = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (!inString && line.AsSpan(i).StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                inTriple = !inTriple;
                i += 2;
                continue;
            }

            var current = line[i];
            if (!inTriple && current == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }

            if (inString || inTriple)
            {
                continue;
            }

            var isBoundary = i == 0 || char.IsWhiteSpace(line[i - 1]);
            if (current == '#' && isBoundary)
            {
                return i;
            }

            if (current == '/' && i + 1 < line.Length && line[i + 1] == '/' && isBoundary)
            {
                return i;
            }

            if (current == ':' && i + 1 < line.Length && line[i + 1] == ':' && isBoundary)
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetDocsCommandDescription(string line)
    {
        if (line is "{" or "}")
        {
            return string.Empty;
        }

        if (line.StartsWith("on ", StringComparison.OrdinalIgnoreCase))
        {
            return GetEventDescription(line);
        }

        if (line.StartsWith("if ", StringComparison.OrdinalIgnoreCase))
        {
            return "Runs the block only when the condition is true.";
        }

        if (line.StartsWith("every ", StringComparison.OrdinalIgnoreCase))
        {
            return "Repeats the block on a timer.";
        }

        if (line.StartsWith("after ", StringComparison.OrdinalIgnoreCase))
        {
            return "Runs the block once after a delay.";
        }

        if (line.StartsWith("shortcut ", StringComparison.OrdinalIgnoreCase))
        {
            return "Runs the block when the shortcut is pressed.";
        }

        if (line.StartsWith("wait ", StringComparison.OrdinalIgnoreCase))
        {
            return "Pauses the script for the selected duration.";
        }

        var match = System.Text.RegularExpressions.Regex.Match(line, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\b");
        if (!match.Success)
        {
            return string.Empty;
        }

        var commandName = match.Groups["name"].Value.ToLowerInvariant();
        if (commandName is "app.setbadge" or "badge.set" or "app.setbadgestatus" or "badge.status" or "app.setbadgedot" or "badge.dot")
        {
            return GetBadgeValueDescription(TryGetFirstArgument(line));
        }

        return commandName switch
        {
            "app.quit" => "Closes the generated app.",
            "app.restart" => "Restarts the generated app.",
            "app.log" => "Writes a message to the app console.",
            "app.openexternal" => "Opens a URL in the system browser.",
            "app.setbadge" or "badge.set" => "Sets a taskbar badge.",
            "app.setbadgecount" or "badge.count" => "Shows a numeric taskbar badge.",
            "app.setbadgedot" or "badge.dot" => "Shows a colored taskbar dot badge.",
            "app.setbadgetext" or "badge.text" => "Shows a short taskbar text badge.",
            "app.setbadgestatus" or "badge.status" => "Shows a taskbar status badge.",
            "app.setbadgeicon" or "badge.icon" => "Uses an image file as the taskbar badge.",
            "app.setbadgefromsiteicon" or "badge.siteicon" => "Uses the current website icon as the taskbar badge; does nothing if no icon can be loaded.",
            "app.clearbadge" or "badge.clear" => "Clears the taskbar badge.",
            "window.settitle" => "Changes the window title.",
            "window.resize" => "Changes the window size.",
            "window.center" => "Moves the window to the center of the screen.",
            "window.maximize" => "Maximizes the window.",
            "window.unmaximize" => "Leaves maximized mode.",
            "window.minimize" => "Minimizes the window.",
            "window.restore" => "Restores the window.",
            "window.fullscreen" => "Turns fullscreen on or off.",
            "window.togglefullscreen" => "Toggles fullscreen.",
            "window.alwaysontop" => "Keeps the window above other windows.",
            "window.setresizable" => "Turns resizing on or off.",
            "window.show" => "Shows the window.",
            "window.hide" => "Hides the window.",
            "window.focus" => "Focuses the window.",
            "window.flash" => "Flashes the taskbar button.",
            "window.setopacity" => "Changes window opacity.",
            "devtools.open" => "Opens Electron DevTools.",
            "devtools.close" => "Closes Electron DevTools.",
            "devtools.toggle" => "Toggles Electron DevTools.",
            "page.reload" => "Reloads the page.",
            "page.reloadignoringcache" => "Reloads the page without cache.",
            "page.back" => "Navigates back.",
            "page.forward" => "Navigates forward.",
            "page.stop" => "Stops loading.",
            "page.load" => "Loads a URL in the app window.",
            "page.setzoom" => "Sets page zoom.",
            "page.getzoom" => "Writes the current zoom level to the console.",
            "page.zoomin" => "Zooms in.",
            "page.zoomout" => "Zooms out.",
            "page.resetzoom" => "Resets zoom.",
            "page.print" => "Opens the print flow.",
            "page.saveaspdf" => "Saves the current page as a PDF file.",
            "page.screenshot" => "Saves a screenshot of the current page.",
            "page.find" => "Finds text inside the page.",
            "page.clearfind" => "Clears the current find highlight.",
            "page.click" or "click" => "Clicks an element.",
            "page.clickall" => "Clicks all matching elements.",
            "page.type" or "type" => "Types text into an element.",
            "page.setvalue" => "Sets an input value.",
            "page.clear" => "Clears an input value.",
            "page.focus" => "Focuses an element.",
            "page.blur" => "Blurs an element.",
            "page.text" or "querytext" => "Writes element text to the console.",
            "page.html" or "queryhtml" => "Writes element HTML to the console.",
            "page.attr" => "Writes an element attribute to the console.",
            "page.setattr" => "Sets an element attribute.",
            "page.exists" or "query" => "Writes whether an element exists.",
            "queryall" => "Writes the number of matching elements.",
            "page.waitfor" or "waitfor" => "Waits until an element exists.",
            "page.remove" or "remove" => "Removes elements from the page.",
            "page.scrollto" => "Scrolls to a page coordinate.",
            "page.scrolltop" => "Scrolls to the top.",
            "page.scrollbottom" => "Scrolls to the bottom.",
            "page.addclass" => "Adds a CSS class to an element.",
            "page.removeclass" => "Removes a CSS class from an element.",
            "page.toggleclass" => "Toggles a CSS class on an element.",
            "settext" => "Sets element text.",
            "sethtml" => "Sets element HTML.",
            "setstyle" => "Sets one CSS style property.",
            "js.run" or "runjs" or "inject" => "Runs JavaScript in the page.",
            "js.eval" => "Evaluates JavaScript in the page.",
            "js.file" => "Runs JavaScript from a file.",
            "css.inject" => "Injects CSS into the page.",
            "css.file" => "Injects CSS from a file.",
            "css.hide" or "hide" => "Hides matching elements.",
            "css.show" => "Shows matching elements.",
            "css.theme" => "Injects a light, dark, or custom theme.",
            "css.removeall" => "Removes CSS previously injected by WooScript.",
            "navigation.block" => "Blocks matching navigation URLs.",
            "navigation.allow" => "Allows matching navigation URLs.",
            "navigation.redirect" => "Redirects matching navigation URLs.",
            "navigation.openexternal" => "Opens matching navigation URLs externally.",
            "navigation.locktomain" => "Locks navigation to the original URL.",
            "navigation.unlock" => "Unlocks navigation.",
            "navigation.setnewlinks" => "Controls how new window links are handled.",
            "navigation.cancel" => "Cancels the next navigation.",
            "downloads.allow" => "Allows downloads.",
            "downloads.block" => "Blocks downloads.",
            "downloads.setfolder" => "Sets the download folder.",
            "downloads.askwheretosave" => "Toggles the save location prompt.",
            "notify" => "Shows a notification.",
            "alert" => "Shows an alert dialog.",
            "dialog.info" => "Shows an info dialog.",
            "dialog.warning" => "Shows a warning dialog.",
            "dialog.error" => "Shows an error dialog.",
            "dialog.confirm" => "Shows a confirmation dialog and logs the result.",
            "toast" => "Shows a quick notification.",
            "clipboard.writetext" => "Copies text to the clipboard.",
            "clipboard.readtext" => "Reads clipboard text into the log.",
            "clipboard.clear" => "Clears the clipboard.",
            "storage.local.set" => "Stores a persistent value in localStorage.",
            "storage.local.get" => "Reads a localStorage value into the console.",
            "storage.local.remove" => "Removes a localStorage value.",
            "storage.local.clear" => "Clears localStorage.",
            "storage.session.set" => "Stores a sessionStorage value.",
            "storage.session.get" => "Reads a sessionStorage value into the console.",
            "storage.session.remove" => "Removes a sessionStorage value.",
            "storage.session.clear" => "Clears sessionStorage.",
            "cookies.set" => "Sets a cookie.",
            "cookies.get" => "Reads a cookie into the console.",
            "cookies.remove" => "Removes a cookie.",
            "cookies.clear" => "Clears cookies.",
            "cache.clear" => "Clears cache.",
            "useragent.set" => "Changes the session user agent.",
            "useragent.reset" => "Resets the session user agent.",
            _ => "Runs this WooScript command."
        };
    }

    private static string GetEventDescription(string line)
    {
        var eventName = line[3..].Split('{')[0].Trim().ToLowerInvariant();
        return eventName switch
        {
            "app.ready" => "Runs after the generated app starts.",
            "app.close" => "Runs before the generated app closes.",
            "window.ready" => "Runs when the app window is ready.",
            "window.focus" => "Runs when the user focuses the window.",
            "window.blur" => "Runs when the user leaves the window.",
            "page.loading" => "Runs when page navigation starts.",
            "page.ready" or "page.loaded" => "Runs after the page finishes loading.",
            "page.error" => "Runs when page loading fails.",
            "page.titlechanged" => "Runs when the page title changes.",
            "page.urlchanged" => "Runs when the page URL changes.",
            "navigation.start" => "Runs when navigation starts.",
            "navigation.finish" => "Runs after navigation finishes.",
            "download.start" => "Runs when a download starts.",
            "download.finish" => "Runs when a download finishes successfully.",
            "download.error" => "Runs when a download fails or is canceled.",
            _ when eventName.StartsWith("url.match", StringComparison.Ordinal) => "Runs when the current URL matches the pattern.",
            _ when eventName.StartsWith("url.contains", StringComparison.Ordinal) => "Runs when the current URL contains the text.",
            _ when eventName.StartsWith("title.match", StringComparison.Ordinal) => "Runs when the page title matches the pattern.",
            _ when eventName.StartsWith("title.contains", StringComparison.Ordinal) => "Runs when the page title contains the text.",
            _ when eventName.StartsWith("selector.exists", StringComparison.Ordinal) => "Runs when the selector exists on the page.",
            _ => "Runs when this event happens."
        };
    }

    private static string TryGetFirstArgument(string line)
    {
        var start = line.IndexOf('(');
        var end = line.LastIndexOf(')');
        if (start < 0 || end <= start)
        {
            return string.Empty;
        }

        var value = line[(start + 1)..end].Trim();
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
        {
            value = value[1..^1];
        }

        return value;
    }

    private static string GetBadgeValueDescription(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (int.TryParse(normalized, out _))
        {
            return "Shows that number on the taskbar badge.";
        }

        return normalized switch
        {
            "99plus" or "99+" => "Shows 99+ on the taskbar badge.",
            "dot" => "Shows a plain dot badge.",
            "green" => "Shows a green availability dot.",
            "red" => "Shows a red busy/error dot.",
            "yellow" => "Shows a yellow away/warning dot.",
            "orange" => "Shows an orange attention dot.",
            "blue" => "Shows a blue active/work dot.",
            "purple" => "Shows a purple custom status dot.",
            "white" => "Shows a neutral white dot.",
            "loading" => "Shows a loading status badge.",
            "sync" => "Shows a syncing status badge.",
            "recording" => "Shows a recording status badge.",
            "muted" => "Shows a muted status badge.",
            "live" => "Shows a LIVE status badge.",
            "error" => "Shows an error badge.",
            "warning" => "Shows a warning badge.",
            "info" => "Shows an information badge.",
            "lock" => "Shows a locked status badge.",
            "unlock" => "Shows an unlocked status badge.",
            "star" => "Shows a starred/favorite badge.",
            "fire" => "Shows a trending/hot badge.",
            "time" => "Shows a waiting/time badge.",
            "download" => "Shows a download badge.",
            "upload" => "Shows an upload badge.",
            "update" => "Shows an update available badge.",
            "battery" => "Shows a battery/status badge.",
            "playmode" => "Shows a play badge.",
            "pausemode" => "Shows a pause badge.",
            "alertmode" => "Shows an alert badge.",
            "successmode" => "Shows a success badge.",
            "gamemode" => "Shows a game/do not disturb badge.",
            "dnd" => "Shows a do not disturb badge.",
            _ => "Sets the taskbar badge to this custom value."
        };
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    internal static string CreateCustomScriptEditorHtml(string initialCode)
    {
        var initialJson = JsonSerializer.Serialize(initialCode);
        var monacoDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Monaco");
        var monacoVsPath = Path.Combine(monacoDirectory, "vs");
        var monacoLoaderPath = Path.Combine(monacoVsPath, "loader.js");
        var monacoEditorWorkerPath = Directory.Exists(Path.Combine(monacoVsPath, "assets"))
            ? Directory.GetFiles(Path.Combine(monacoVsPath, "assets"), "editor.worker-*.js")
                .Select(path => Path.GetFileName(path))
                .FirstOrDefault()
            : null;
        var monacoVsUrl = File.Exists(monacoLoaderPath)
            ? "https://woo-monaco.local/vs"
            : string.Empty;
        var monacoLoaderUrl = File.Exists(monacoLoaderPath)
            ? "https://woo-monaco.local/vs/loader.js"
            : string.Empty;
        var monacoEditorWorkerUrl = !string.IsNullOrWhiteSpace(monacoEditorWorkerPath)
            ? $"https://woo-monaco.local/vs/assets/{monacoEditorWorkerPath}"
            : string.Empty;
        var monacoVsJson = JsonSerializer.Serialize(monacoVsUrl);
        var monacoLoaderJson = JsonSerializer.Serialize(monacoLoaderUrl);
        var monacoEditorWorkerJson = JsonSerializer.Serialize(monacoEditorWorkerUrl);

        return $$""""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta http-equiv="X-UA-Compatible" content="IE=edge">
              <style>
                html, body {
                  width: 100%;
                  height: 100%;
                  margin: 0;
                  overflow: hidden;
                  position: relative;
                  background: #101216;
                  color: #e8ebf0;
                  font-family: "Cascadia Code", Consolas, monospace;
                }

                .shell {
                  display: grid;
                  grid-template-columns: 54px 1fr;
                  height: 100%;
                  border-radius: 8px;
                  overflow: hidden;
                }

                #monaco {
                  display: none;
                  width: 100%;
                  height: 100%;
                }

                #lines {
                  padding: 12px 8px;
                  background: #0b0d10;
                  color: #6f7787;
                  text-align: right;
                  user-select: none;
                  line-height: 20px;
                  font-size: 13px;
                  overflow: hidden;
                  white-space: pre;
                }

                #editor {
                  width: 100%;
                  height: 100%;
                  box-sizing: border-box;
                  resize: none;
                  border: 0;
                  outline: none;
                  padding: 12px;
                  background: #101216;
                  color: #e8ebf0;
                  caret-color: #7cc4ff;
                  line-height: 20px;
                  font-size: 13px;
                  font-family: "Cascadia Code", Consolas, monospace;
                  white-space: pre;
                  overflow: auto;
                  tab-size: 2;
                }

                #editor::selection {
                  background: #315b85;
                }

                #diagnostics {
                  display: none;
                  position: absolute;
                  left: 10px;
                  right: 10px;
                  bottom: 10px;
                  z-index: 30;
                  max-height: 84px;
                  overflow: auto;
                  padding: 8px 10px;
                  border-radius: 7px;
                  border: 1px solid rgba(231, 72, 86, 0.45);
                  background: rgba(19, 22, 29, 0.96);
                  box-shadow: 0 10px 28px rgba(0, 0, 0, 0.34);
                  font: 12px/18px "Cascadia Code", Consolas, monospace;
                }

                .woo-diagnostic-row {
                  color: #ff7d89;
                  white-space: pre-wrap;
                }

                .woo-diagnostic-row.warning {
                  color: #f9d56e;
                }

                .woo-error-line {
                  background: rgba(231, 72, 86, 0.16) !important;
                }

                .woo-warning-line {
                  background: rgba(249, 241, 165, 0.12) !important;
                }

                .woo-diagnostic-inline-error {
                  color: #ff7d89 !important;
                  font-style: italic;
                }

                .woo-diagnostic-inline-warning {
                  color: #f9d56e !important;
                  font-style: italic;
                }

                .woo-confetti {
                  position: fixed;
                  inset: 0;
                  z-index: 20;
                  pointer-events: none;
                  overflow: hidden;
                }

                .woo-confetti-message {
                  position: absolute;
                  left: 50%;
                  top: 42%;
                  transform: translate(-50%, -50%);
                  color: #e8ebf0;
                  font: 700 26px "Segoe UI", Arial, sans-serif;
                  text-shadow: 0 8px 28px rgba(0, 0, 0, 0.55);
                }

                .woo-confetti-piece {
                  position: absolute;
                  top: -16px;
                  width: 8px;
                  height: 14px;
                  border-radius: 2px;
                  animation: woo-confetti-fall 1200ms ease-out forwards;
                }

                @keyframes woo-confetti-fall {
                  to {
                    transform: translate3d(var(--dx), 105vh, 0) rotate(720deg);
                    opacity: 0;
                  }
                }
              </style>
            </head>
            <body>
              <div id="monaco"></div>
              <div class="shell">
                <div id="lines">1</div>
                <textarea id="editor" spellcheck="false" autocomplete="off" autocorrect="off" autocapitalize="off"></textarea>
              </div>
              <div id="diagnostics"></div>
              <script>
                const editor = document.getElementById('editor');
                const lines = document.getElementById('lines');
                const shell = document.querySelector('.shell');
                const monacoHost = document.getElementById('monaco');
                const diagnosticsPanel = document.getElementById('diagnostics');
                let postTimer = null;
                const initialValue = {{initialJson}};
                const monacoVsUrl = {{monacoVsJson}};
                const monacoLoaderUrl = {{monacoLoaderJson}};
                const monacoEditorWorkerUrl = {{monacoEditorWorkerJson}};
                let monacoEditor = null;
                let diagnosticDecorations = [];
                let pendingDiagnostics = [];

                function updateLines() {
                  const count = Math.max(1, editor.value.split(/\r\n|\r|\n/).length);
                  let text = '';
                  for (let i = 1; i <= count; i += 1) {
                    text += i + (i === count ? '' : '\n');
                  }

                  lines.textContent = text;
                }

                function post(type) {
                  if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type, value: window.wooEditorGetValue() });
                  }
                }

                function queueChanged() {
                  updateLines();
                  if (monacoEditor && pendingDiagnostics.length > 0) {
                    requestAnimationFrame(() => window.wooEditorSetDiagnostics(pendingDiagnostics));
                  }

                  clearTimeout(postTimer);
                  postTimer = setTimeout(() => post('changed'), 80);
                }

                window.wooEditorGetValue = () => monacoEditor ? monacoEditor.getValue() : editor.value;
                window.wooEditorSetValue = (value) => {
                  if (monacoEditor) {
                    monacoEditor.setValue(value || '');
                    return;
                  }

                  editor.value = value || '';
                  updateLines();
                };
                window.wooEditorSetDiagnostics = (diagnostics) => {
                  pendingDiagnostics = Array.isArray(diagnostics) ? diagnostics : [];
                  diagnosticsPanel.innerHTML = '';
                  if (pendingDiagnostics.length > 0) {
                    for (const diagnostic of pendingDiagnostics) {
                      const row = document.createElement('div');
                      row.className = 'woo-diagnostic-row' + (diagnostic.severity === 'warning' ? ' warning' : '');
                      row.textContent = `Line ${diagnostic.line || 1}: ${diagnostic.message || ''}`;
                      diagnosticsPanel.appendChild(row);
                    }
                    diagnosticsPanel.style.display = 'block';
                  } else {
                    diagnosticsPanel.style.display = 'none';
                  }

                  if (!monacoEditor || !window.monaco) {
                    return;
                  }

                  const model = monacoEditor.getModel();
                  const markers = pendingDiagnostics.map((diagnostic) => ({
                    startLineNumber: Math.max(1, Math.min(diagnostic.line || 1, model.getLineCount())),
                    startColumn: 1,
                    endLineNumber: Math.max(1, Math.min(diagnostic.line || 1, model.getLineCount())),
                    endColumn: model.getLineMaxColumn(Math.max(1, Math.min(diagnostic.line || 1, model.getLineCount()))),
                    message: diagnostic.message || '',
                    severity: diagnostic.severity === 'warning'
                      ? monaco.MarkerSeverity.Warning
                      : monaco.MarkerSeverity.Error
                  }));
                  monaco.editor.setModelMarkers(model, 'wooscript', markers);
                  diagnosticDecorations = monacoEditor.deltaDecorations(
                    diagnosticDecorations,
                    pendingDiagnostics.map((diagnostic) => {
                      const line = Math.max(1, Math.min(diagnostic.line || 1, model.getLineCount()));
                      const lastColumn = model.getLineMaxColumn(line);
                      return {
                        range: new monaco.Range(line, lastColumn, line, lastColumn),
                        options: {
                          isWholeLine: true,
                          className: diagnostic.severity === 'warning' ? 'woo-warning-line' : 'woo-error-line',
                          after: {
                            content: '  ' + (diagnostic.message || ''),
                            inlineClassName: diagnostic.severity === 'warning'
                              ? 'woo-diagnostic-inline-warning'
                              : 'woo-diagnostic-inline-error'
                          },
                          hoverMessage: { value: diagnostic.message || '' }
                        }
                      };
                    })
                  );
                };
                window.wooEditorCelebrateClear = () => {
                  const existing = document.querySelector('.woo-confetti');
                  if (existing) {
                    existing.remove();
                  }

                  const overlay = document.createElement('div');
                  overlay.className = 'woo-confetti';
                  const message = document.createElement('div');
                  message.className = 'woo-confetti-message';
                  message.textContent = "Bro i'm cleaning!";
                  overlay.appendChild(message);

                  const colors = ['#569cd6', '#ce9178', '#b5cea8', '#dcdcaa', '#c586c0', '#4ec9b0'];
                  for (let i = 0; i < 180; i += 1) {
                    const piece = document.createElement('div');
                    piece.className = 'woo-confetti-piece';
                    piece.style.left = `${Math.random() * 100}%`;
                    piece.style.background = colors[i % colors.length];
                    piece.style.setProperty('--dx', `${Math.round((Math.random() - 0.5) * 220)}px`);
                    piece.style.animationDelay = `${Math.random() * 220}ms`;
                    overlay.appendChild(piece);
                  }

                  document.body.appendChild(overlay);
                  setTimeout(() => overlay.remove(), 1600);
                };

                editor.value = initialValue || '';
                updateLines();
                editor.addEventListener('input', queueChanged);
                editor.addEventListener('scroll', () => {
                  lines.scrollTop = editor.scrollTop;
                });
                editor.addEventListener('keydown', (event) => {
                  if (event.key === 'Tab') {
                    event.preventDefault();
                    const start = editor.selectionStart;
                    const end = editor.selectionEnd;
                    editor.value = editor.value.slice(0, start) + '  ' + editor.value.slice(end);
                    editor.selectionStart = editor.selectionEnd = start + 2;
                    queueChanged();
                  }

                  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
                    event.preventDefault();
                    post('saved');
                  }
                });

                function loadScript(src, onload, onerror) {
                  const script = document.createElement('script');
                  script.src = src;
                  script.onload = onload;
                  script.onerror = onerror;
                  document.head.appendChild(script);
                }

                function createFallbackEditor() {
                  shell.style.display = 'grid';
                  monacoHost.style.display = 'none';
                  post('ready');
                }

                function createMonacoEditor() {
                  try {
                    if (!window.require || !window.monaco) {
                      createFallbackEditor();
                      return;
                    }

                    shell.style.display = 'none';
                    monacoHost.style.display = 'block';
                    monaco.languages.register({ id: 'wooscript' });
                    monaco.languages.setMonarchTokensProvider('wooscript', {
                      tokenizer: {
                        root: [
                          [/"""/, { token: 'string.quote', next: '@multiline' }],
                          [/"([^"\\]|\\.)*"/, 'string'],
                          [/(^|\s)#.*$/, 'comment'],
                          [/(^|\s)\/\/.*$/, 'comment'],
                          [/(^|\s)::.*$/, 'comment'],
                          [/\b(on|if|else|let|true|false|wait|every|after|shortcut|contains|startsWith|endsWith|matches)\b/, 'keyword'],
                          [/\b(app|badge|window|page|js|css|navigation|downloads|dialog|clipboard|storage|cookies|cache|devtools|userAgent|selector|title|url)\b/, 'type.identifier'],
                          [/==|!=|<=|>=|&&|\|\||[{}()[\].,]/, 'delimiter'],
                          [/[=<>!+\-*/%]/, 'operator'],
                          [/\b\d+(ms|s|m)?\b/, 'number'],
                          [/[A-Za-z_][A-Za-z0-9_]*(?=\()/, 'function']
                        ],
                        multiline: [
                          [/"""/, { token: 'string.quote', next: '@pop' }],
                          [/./, 'string']
                        ]
                      }
                    });

                    const commandNames = [
                      'wait',
                      'app.quit', 'app.restart', 'app.showMessage', 'app.log', 'app.openExternal',
                      'app.setBadge', 'app.setBadgeCount', 'app.setBadgeDot', 'app.setBadgeText', 'app.setBadgeStatus', 'app.setBadgeIcon', 'app.setBadgeFromSiteIcon', 'app.clearBadge',
                      'badge.set', 'badge.count', 'badge.dot', 'badge.text', 'badge.status', 'badge.icon', 'badge.siteIcon', 'badge.clear',
                      'window.setTitle', 'window.resize', 'window.setWidth', 'window.setHeight', 'window.center', 'window.maximize', 'window.unmaximize',
                      'window.minimize', 'window.restore', 'window.fullscreen', 'window.toggleFullscreen', 'window.alwaysOnTop', 'window.setResizable',
                      'window.show', 'window.hide', 'window.focus', 'window.blur', 'window.flash', 'window.setOpacity',
                      'devtools.open', 'devtools.close', 'devtools.toggle',
                      'page.reload', 'page.reloadIgnoringCache', 'page.back', 'page.forward', 'page.stop', 'page.load', 'page.setZoom', 'page.getZoom',
                      'page.zoomIn', 'page.zoomOut', 'page.resetZoom', 'page.print', 'page.saveAsPdf', 'page.screenshot', 'page.find', 'page.clearFind',
                      'page.click', 'page.clickAll', 'page.type', 'page.setValue', 'page.clear', 'page.focus', 'page.blur', 'page.text', 'page.html',
                      'page.attr', 'page.setAttr', 'page.exists', 'page.waitFor', 'page.remove', 'page.scrollTo', 'page.scrollTop', 'page.scrollBottom',
                      'page.addClass', 'page.removeClass', 'page.toggleClass',
                      'js.run', 'js.eval', 'js.file', 'runjs', 'inject',
                      'css.inject', 'css.file', 'css.removeAll', 'css.hide', 'css.show', 'css.theme',
                      'navigation.block', 'navigation.allow', 'navigation.redirect', 'navigation.openExternal', 'navigation.lockToMain',
                      'navigation.unlock', 'navigation.setNewLinks', 'navigation.cancel',
                      'downloads.allow', 'downloads.block', 'downloads.setFolder', 'downloads.askWhereToSave',
                      'notify', 'alert', 'dialog.info', 'dialog.warning', 'dialog.error', 'dialog.confirm', 'toast',
                      'clipboard.writeText', 'clipboard.readText', 'clipboard.clear',
                      'storage.local.set', 'storage.local.get', 'storage.local.remove', 'storage.local.clear',
                      'storage.session.set', 'storage.session.get', 'storage.session.remove', 'storage.session.clear',
                      'cookies.set', 'cookies.get', 'cookies.remove', 'cookies.clear', 'cache.clear',
                      'userAgent.set', 'userAgent.reset',
                      'query', 'queryText', 'queryHtml', 'queryAll', 'setText', 'setHtml', 'setStyle', 'click', 'type', 'waitFor', 'hide', 'remove'
                    ];

                    const noParens = new Set(['wait']);
                    const suggestions = commandNames.map((name) => ({
                      label: name,
                      insertText: noParens.has(name) ? `${name} ` : `${name}()`,
                      kind: name === 'wait'
                        ? monaco.languages.CompletionItemKind.Keyword
                        : monaco.languages.CompletionItemKind.Method
                    }));

                    monaco.languages.registerCompletionItemProvider('wooscript', {
                      provideCompletionItems: () => ({
                        suggestions: suggestions.map((entry) => ({
                          label: entry.label,
                          kind: entry.kind,
                          insertText: entry.insertText
                        }))
                      })
                    });

                    monacoEditor = monaco.editor.create(monacoHost, {
                      value: editor.value,
                      language: 'wooscript',
                      theme: 'vs-dark',
                      minimap: { enabled: false },
                      automaticLayout: true,
                      wordWrap: 'on',
                      lineNumbers: 'on',
                      bracketPairColorization: { enabled: true },
                      autoClosingBrackets: 'always',
                      autoClosingQuotes: 'always',
                      suggestOnTriggerCharacters: true,
                      wordBasedSuggestions: 'off',
                      fontFamily: 'Cascadia Code, Consolas, monospace',
                      fontSize: 13,
                      lineHeight: 20,
                      padding: { top: 10, bottom: 10 }
                    });

                    monacoEditor.onDidChangeModelContent(queueChanged);
                    monacoEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => post('saved'));
                    window.wooEditorSetDiagnostics(pendingDiagnostics);
                    post('ready');
                  } catch {
                    createFallbackEditor();
                  }
                }

                if (monacoLoaderUrl) {
                  window.MonacoEnvironment = {
                    getWorkerUrl: () => monacoEditorWorkerUrl || ''
                  };
                  loadScript(monacoLoaderUrl, () => {
                    try {
                      window.require.config({ paths: { vs: monacoVsUrl } });
                      window.require(['vs/editor/editor.main'], createMonacoEditor, createFallbackEditor);
                    } catch {
                      createFallbackEditor();
                    }
                  }, createFallbackEditor);
                } else {
                  createFallbackEditor();
                }
              </script>
            </body>
            </html>
            """";
    }

    private sealed class EditorMessage
    {
        public string? Type { get; set; }
        public string? Value { get; set; }
    }

    private static SolidColorBrush GetLogBrush(string severity)
    {
        return severity switch
        {
            "Black" => new SolidColorBrush(ColorHelper.FromArgb(255, 12, 12, 12)),
            "DarkRed" => new SolidColorBrush(ColorHelper.FromArgb(255, 197, 15, 31)),
            "DarkGreen" => new SolidColorBrush(ColorHelper.FromArgb(255, 19, 161, 14)),
            "DarkYellow" => new SolidColorBrush(ColorHelper.FromArgb(255, 193, 156, 0)),
            "DarkBlue" => new SolidColorBrush(ColorHelper.FromArgb(255, 0, 55, 218)),
            "DarkMagenta" => new SolidColorBrush(ColorHelper.FromArgb(255, 136, 23, 152)),
            "DarkCyan" => new SolidColorBrush(ColorHelper.FromArgb(255, 58, 150, 221)),
            "Gray" => new SolidColorBrush(ColorHelper.FromArgb(255, 204, 204, 204)),
            "DarkGray" => new SolidColorBrush(ColorHelper.FromArgb(255, 118, 118, 118)),
            "Red" => new SolidColorBrush(ColorHelper.FromArgb(255, 231, 72, 86)),
            "Green" => new SolidColorBrush(ColorHelper.FromArgb(255, 22, 198, 12)),
            "Yellow" => new SolidColorBrush(ColorHelper.FromArgb(255, 249, 241, 165)),
            "Blue" => new SolidColorBrush(ColorHelper.FromArgb(255, 59, 120, 255)),
            "Magenta" => new SolidColorBrush(ColorHelper.FromArgb(255, 180, 0, 158)),
            "Cyan" => new SolidColorBrush(ColorHelper.FromArgb(255, 97, 214, 214)),
            "White" => new SolidColorBrush(ColorHelper.FromArgb(255, 242, 242, 242)),
            _ => new SolidColorBrush(ColorHelper.FromArgb(255, 232, 235, 240))
        };
    }
}
