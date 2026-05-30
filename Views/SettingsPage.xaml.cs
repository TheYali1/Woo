using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Woo_.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Woo_.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; private set; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    private async void BrowseDefaultOutput_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null)
        {
            ViewModel.DefaultOutputDirectory = folder.Path;
        }
    }

    private async void BrowseFallbackIcon_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickIconAsync();
        if (file is not null)
        {
            ViewModel.FallbackIconPath = file.Path;
        }
    }

    private async void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"woo-settings-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await App.SettingsService.ExportAsync(file.Path);
        }
    }

    private async void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await App.SettingsService.ImportAsync(file.Path);
            ViewModel = new SettingsViewModel();
            DataContext = ViewModel;
            App.MainWindow?.ApplyTheme();
        }
    }

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Reset all settings?",
            Content = "Woo! will restore defaults. History records are not deleted.",
            MinWidth = 520,
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Reset();
            ViewModel = new SettingsViewModel();
            DataContext = ViewModel;
            App.MainWindow?.ApplyTheme();
        }
    }

    private void UseCurrentDefaultUserAgent_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UseCurrentUserAgent();
    }

    private static async Task<Windows.Storage.StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        return await picker.PickSingleFolderAsync();
    }

    private static async Task<Windows.Storage.StorageFile?> PickIconAsync()
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
        return await picker.PickSingleFileAsync();
    }

}
