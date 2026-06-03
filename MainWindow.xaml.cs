using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using Woo_.Models;
using Woo_.Views;
using Windows.Graphics;

namespace Woo_;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;

    public MainWindow()
    {
        InitializeComponent();

        App.MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(App.MainWindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Title = "Woo!";

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }

        ApplyTheme();
        RestoreWindowBounds();

        RootNavigation.SelectedItem = HomeNavItem;
        ContentFrame.Navigate(typeof(HomePage));
        RootGrid.Loaded += RootGrid_Loaded;

        Closed += MainWindow_Closed;
    }

    public void NavigateToHome(BuildConfiguration? configuration = null)
    {
        RootNavigation.SelectedItem = HomeNavItem;
        ContentFrame.Navigate(typeof(HomePage), configuration);
    }

    public void NavigateToBuildLog(BuildRequest? request = null)
    {
        RootNavigation.SelectedItem = null;
        ContentFrame.Navigate(typeof(BuildLogPage), request);
    }

    public void ApplyTheme()
    {
        RootGrid.RequestedTheme = App.SettingsService.Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        await ShowStartupDialogsAsync();
    }

    private async Task ShowStartupDialogsAsync()
    {
        var xamlRoot = RootGrid.XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var settings = App.SettingsService.Settings;
        if (settings.CheckUpdatesOnStartup)
        {
            var result = await App.UpdateService.CheckForUpdatesAsync();
            if (!result.Success || !result.IsUpdateAvailable)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Woo! update available",
                Content = $"Version {result.LatestVersion} is available. You have version {result.CurrentVersion}.",
                PrimaryButtonText = "Open GitHub",
                CloseButtonText = "Later"
            };
            var dialogResult = await dialog.ShowAsync();
            if (dialogResult == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = result.ReleaseUrl,
                    UseShellExecute = true
                });
            }
        }
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void RestoreWindowBounds()
    {
        try
        {
            var settings = App.SettingsService.Settings;
            var width = Math.Max(settings.LastWindowWidth, 1180);
            var height = Math.Max(settings.LastWindowHeight, 760);
            _appWindow.Resize(new SizeInt32(width, height));

            if (settings.LastWindowX >= 0 && settings.LastWindowY >= 0)
            {
                _appWindow.Move(new PointInt32(settings.LastWindowX, settings.LastWindowY));
            }
        }
        catch
        {
            // Invalid saved bounds should not prevent Woo! from opening.
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SaveWindowBounds();
    }

    private void SaveWindowBounds()
    {
        try
        {
            if (IsMinimized() || IsMaximized())
            {
                return;
            }

            var size = _appWindow.Size;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            var settings = App.SettingsService.Settings;
            settings.LastWindowWidth = Math.Max(size.Width, 1180);
            settings.LastWindowHeight = Math.Max(size.Height, 760);
            settings.LastWindowX = _appWindow.Position.X;
            settings.LastWindowY = _appWindow.Position.Y;
            App.SettingsService.Save();
        }
        catch
        {
            // Window movement can fire while the OS is changing presenter state.
            // Bounds persistence should never be able to crash Woo!.
        }
    }

    private bool IsMinimized()
    {
        return _appWindow.Presenter is OverlappedPresenter presenter &&
               presenter.State == OverlappedPresenterState.Minimized;
    }

    private bool IsMaximized()
    {
        return _appWindow.Presenter is OverlappedPresenter presenter &&
               presenter.State == OverlappedPresenterState.Maximized;
    }
}
