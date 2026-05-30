using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.Specialized;
using System.Diagnostics;
using Woo_.Helpers;
using Woo_.Models;
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

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        _miniAutoScroll = true;
        ViewModel.BuildLogEntries.CollectionChanged += BuildLogEntries_CollectionChanged;
        MiniLogScrollViewer.ViewChanged += MiniLogScrollViewer_ViewChanged;
        RenderMiniLogEntries(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        while (_miniRenderedLogEntries < ViewModel.BuildLogEntries.Count)
        {
            AppendMiniLogEntry(ViewModel.BuildLogEntries[_miniRenderedLogEntries]);
        }

        QueueMiniScrollToBottom();
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.BuildLogEntries.CollectionChanged -= BuildLogEntries_CollectionChanged;
        MiniLogScrollViewer.ViewChanged -= MiniLogScrollViewer_ViewChanged;
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
