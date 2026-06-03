using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using System.Diagnostics;
using System.ComponentModel;
using Woo_.Models;
using Woo_.Services;
using Woo_.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Woo_.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryViewModel ViewModel { get; } = new();

    public HistoryPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += HistoryPage_Loaded;
    }

    private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        RenderCustomScriptPreview();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SelectedRecord))
        {
            RenderCustomScriptPreview();
        }
    }

    private void RenderCustomScriptPreview()
    {
        if (CustomScriptPreview is null)
        {
            return;
        }

        CustomScriptPreview.Blocks.Clear();
        var paragraph = new Paragraph();
        var code = ViewModel.SelectedRecord?.CustomScriptCode ?? string.Empty;
        foreach (var rawLine in code.Replace("\r\n", "\n").Split('\n'))
        {
            AppendHighlightedLine(paragraph, rawLine);
            paragraph.Inlines.Add(new LineBreak());
        }

        CustomScriptPreview.Blocks.Add(paragraph);
    }

    private static void AppendHighlightedLine(Paragraph paragraph, string line)
    {
        var commentIndex = FindCommentIndex(line);
        var codePart = commentIndex >= 0 ? line[..commentIndex] : line;
        var commentPart = commentIndex >= 0 ? line[commentIndex..] : string.Empty;

        var tokenPattern = "\"(?:\\\\.|[^\"])*\"|\\b\\d+(?:ms|s|m)?\\b|\\b(on|if|else|let|true|false|wait|every|after|shortcut|contains|startsWith|endsWith|matches)\\b|\\b(app|badge|window|page|js|css|navigation|downloads|dialog|clipboard|storage|cookies|cache|devtools|userAgent|selector|title|url)\\b";
        var index = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(codePart, tokenPattern))
        {
            if (match.Index > index)
            {
                AddRun(paragraph, codePart[index..match.Index], "#E8EBF0");
            }

            var color = match.Value.StartsWith('"')
                ? "#CE9178"
                : char.IsDigit(match.Value[0])
                    ? "#B5CEA8"
                    : IsKeyword(match.Value)
                        ? "#C586C0"
                        : "#4EC9B0";
            AddRun(paragraph, match.Value, color);
            index = match.Index + match.Length;
        }

        if (index < codePart.Length)
        {
            AddRun(paragraph, codePart[index..], "#E8EBF0");
        }

        if (!string.IsNullOrEmpty(commentPart))
        {
            AddRun(paragraph, commentPart, "#6A9955");
        }
    }

    private static bool IsKeyword(string value)
    {
        return value is "on" or "if" or "else" or "let" or "true" or "false" or "wait" or "every" or "after" or "shortcut" or "contains" or "startsWith" or "endsWith" or "matches";
    }

    private static void AddRun(Paragraph paragraph, string text, string color)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        paragraph.Inlines.Add(new Run
        {
            Text = text,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(
                255,
                Convert.ToByte(color[1..3], 16),
                Convert.ToByte(color[3..5], 16),
                Convert.ToByte(color[5..7], 16)))
        });
    }

    private static int FindCommentIndex(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (current == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }

            if (inString)
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

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear all history?",
            Content = "This removes Woo! history records only. Exported files are not deleted.",
            MinWidth = 520,
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearAllAsync();
        }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"woo-history-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await ViewModel.ExportCsvAsync(file.Path);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenFolder(GetRecord(sender));
    }

    private void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Rebuild(GetRecord(sender));
    }

    private async void ExportPreset_Click(object sender, RoutedEventArgs e)
    {
        var record = GetRecord(sender);
        if (record is null)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"{record.AppName}.wooapp"
        };
        picker.FileTypeChoices.Add("Woo! App Settings", [".wooapp"]);
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await AppPresetService.ExportAsync(file.Path, ViewModel.CreateConfiguration(record));
        }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CopyUrl(GetRecord(sender));
    }

    private async void DeleteRecord_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteRecordAsync(GetRecord(sender));
    }

    private static ExportRecord? GetRecord(object sender)
    {
        return sender is FrameworkElement element ? element.Tag as ExportRecord : null;
    }

    private void CopySelectedBuildLog_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRecord?.BuildLog is not { Length: > 0 } buildLog)
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(buildLog);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string url || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
    }

    private void OpenLocalPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string path || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return;
        }

        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }
}
