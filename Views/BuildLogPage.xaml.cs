using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.Specialized;
using System.Diagnostics;
using Woo_.Models;
using Woo_.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Woo_.Views;

public sealed partial class BuildLogPage : Page
{
    public HomeViewModel ViewModel { get; } = App.HomeViewModel;
    private ScrollViewer? _logScrollViewer;
    private bool _autoScroll = true;
    private Paragraph? _logParagraph;
    private int _renderedLogEntries;
    private bool _scrollQueued;

    public BuildLogPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += BuildLogPage_Loaded;
        Unloaded += BuildLogPage_Unloaded;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is BuildRequest request)
        {
            await ViewModel.BuildAsync(request);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateToHome(ViewModel.CurrentRequest?.Configuration);
    }

    private void BuildLogPage_Loaded(object sender, RoutedEventArgs e)
    {
        _autoScroll = true;
        _logScrollViewer = LogScrollViewer;
        _logScrollViewer.ViewChanged += LogScrollViewer_ViewChanged;
        ViewModel.BuildLogEntries.CollectionChanged += BuildLogEntries_CollectionChanged;
        RenderLogEntries(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        while (_renderedLogEntries < ViewModel.BuildLogEntries.Count)
        {
            AppendLogEntry(ViewModel.BuildLogEntries[_renderedLogEntries]);
        }

        QueueScrollToBottom();
    }

    private void BuildLogPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_logScrollViewer is not null)
        {
            _logScrollViewer.ViewChanged -= LogScrollViewer_ViewChanged;
        }

        ViewModel.BuildLogEntries.CollectionChanged -= BuildLogEntries_CollectionChanged;
    }

    private void BuildLogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var shouldAutoScroll = _autoScroll;
        RenderLogEntries(e);

        if (shouldAutoScroll)
        {
            QueueScrollToBottom();
        }
    }

    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_logScrollViewer is null)
        {
            return;
        }

        _autoScroll = _logScrollViewer.VerticalOffset >= _logScrollViewer.ScrollableHeight - 8;
    }

    private void ScrollLogToBottom()
    {
        LogRichTextBlock.UpdateLayout();
        LogBottomAnchor.UpdateLayout();
        _logScrollViewer?.UpdateLayout();
        _logScrollViewer?.ChangeView(null, _logScrollViewer.ScrollableHeight, null, true);
        LogBottomAnchor.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 1.0
        });
        _autoScroll = true;
    }

    private async void QueueScrollToBottom()
    {
        if (_scrollQueued)
        {
            return;
        }

        _scrollQueued = true;
        try
        {
            foreach (var delay in new[] { 0, 16, 33, 66, 120 })
            {
                if (delay > 0)
                {
                    await Task.Delay(delay);
                }

                if (!_autoScroll)
                {
                    return;
                }

                _ = DispatcherQueue.TryEnqueue(ScrollLogToBottom);
            }
        }
        finally
        {
            _scrollQueued = false;
        }
    }

    private void RenderLogEntries(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            LogRichTextBlock.Blocks.Clear();
            _logParagraph = null;
            _renderedLogEntries = 0;
            return;
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<BuildLogEntry>())
            {
                AppendLogEntry(item);
            }

            return;
        }

        while (_renderedLogEntries < ViewModel.BuildLogEntries.Count)
        {
            AppendLogEntry(ViewModel.BuildLogEntries[_renderedLogEntries]);
        }
    }

    private void AppendLogEntry(BuildLogEntry entry)
    {
        _logParagraph ??= CreateLogParagraph();
        if (_logParagraph.Inlines.Count > 0)
        {
            _logParagraph.Inlines.Add(new LineBreak());
        }

        _logParagraph.Inlines.Add(new Run
        {
            Text = entry.Text,
            Foreground = GetLogBrush(entry.Severity)
        });
        _renderedLogEntries++;
    }

    private Paragraph CreateLogParagraph()
    {
        var paragraph = new Paragraph();
        LogRichTextBlock.Blocks.Add(paragraph);
        return paragraph;
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

    private async void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"woo-build-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("Text log", [".txt"]);
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await File.WriteAllTextAsync(file.Path, ViewModel.GetLogText());
        }
    }

}
