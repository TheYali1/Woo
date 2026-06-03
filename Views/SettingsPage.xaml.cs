using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using Woo_.ViewModels;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Woo_.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; private set; } = new();
    private bool _defaultScriptEditorInitialized;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeDefaultScriptEditorAsync();
    }

    private async void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        await SyncDefaultScriptEditorAsync();
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
        await SyncDefaultScriptEditorAsync();
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"woo-settings-{DateTime.Now:yyyyMMdd-HHmmss}.woosettings"
        };
        picker.FileTypeChoices.Add("Woo! Settings", [".woosettings"]);
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
        picker.FileTypeFilter.Add(".woosettings");
        InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await App.SettingsService.ImportAsync(file.Path);
            ViewModel = new SettingsViewModel();
            DataContext = ViewModel;
            _defaultScriptEditorInitialized = false;
            await InitializeDefaultScriptEditorAsync();
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
            _defaultScriptEditorInitialized = false;
            await InitializeDefaultScriptEditorAsync();
            App.MainWindow?.ApplyTheme();
        }
    }

    private async void UpdateReleaseLink_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.UpdateReleaseUri is not null)
        {
            await Launcher.LaunchUriAsync(ViewModel.UpdateReleaseUri);
        }
    }

    private void UseCurrentDefaultUserAgent_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UseCurrentUserAgent();
    }

    private async Task InitializeDefaultScriptEditorAsync()
    {
        if (_defaultScriptEditorInitialized)
        {
            return;
        }

        await DefaultCustomScriptEditorWebView.EnsureCoreWebView2Async();
        DefaultCustomScriptEditorWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        DefaultCustomScriptEditorWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        var monacoDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Monaco");
        if (Directory.Exists(monacoDirectory))
        {
            DefaultCustomScriptEditorWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "woo-monaco.local",
                monacoDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        DefaultCustomScriptEditorWebView.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) &&
                    (type.GetString() is "changed" or "saved") &&
                    root.TryGetProperty("value", out var value))
                {
                    ViewModel.DefaultCustomScriptCode = value.GetString() ?? string.Empty;
                }
            }
            catch
            {
            }
        };

        DefaultCustomScriptEditorWebView.NavigateToString(HomePage.CreateCustomScriptEditorHtml(ViewModel.DefaultCustomScriptCode));
        _defaultScriptEditorInitialized = true;
    }

    private async Task SyncDefaultScriptEditorAsync()
    {
        if (!_defaultScriptEditorInitialized)
        {
            return;
        }

        try
        {
            var rawResult = await DefaultCustomScriptEditorWebView.ExecuteScriptAsync("window.wooEditorGetValue && window.wooEditorGetValue()");
            var value = JsonSerializer.Deserialize<string>(rawResult);
            if (value is not null)
            {
                ViewModel.DefaultCustomScriptCode = value;
            }
        }
        catch
        {
        }
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
