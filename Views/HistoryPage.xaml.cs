using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using Woo_.Models;
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
        Loaded += HistoryPage_Loaded;
    }

    private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
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
