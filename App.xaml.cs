using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Woo_.Services;
using Woo_.ViewModels;

namespace Woo_;

public partial class App : Application
{
    private Window? _window;

    public static AppSettingsService SettingsService { get; private set; } = null!;
    public static DatabaseService DatabaseService { get; private set; } = null!;
    public static ToolchainService ToolchainService { get; private set; } = null!;
    public static IconService IconService { get; private set; } = null!;
    public static FaviconService FaviconService { get; private set; } = null!;
    public static BuildService BuildService { get; private set; } = null!;
    public static HomeViewModel HomeViewModel { get; private set; } = null!;
    public static MainWindow? MainWindow { get; private set; }
    public static nint MainWindowHandle { get; set; }

    public App()
    {
        InitializeComponent();
        ConfigureServices();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (await TryRedirectToMainInstanceAsync())
        {
            return;
        }

        TryRegisterNotifications();

        await DatabaseService.InitializeAsync();
        _window = new MainWindow();
        MainWindow = (MainWindow)_window;
        _window.Activate();
    }

    private static async Task<bool> TryRedirectToMainInstanceAsync()
    {
        try
        {
            var currentInstance = AppInstance.GetCurrent();
            var mainInstance = AppInstance.FindOrRegisterForKey("WooMain");
            if (!mainInstance.IsCurrent)
            {
                await mainInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs());
                Environment.Exit(0);
                return true;
            }

            currentInstance.Activated += App_Activated;
            return false;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Woo startup: single-instance registration skipped. {ex.Message}");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Woo startup: single-instance registration skipped. {ex.Message}");
            return false;
        }
    }

    private static void TryRegisterNotifications()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += AppNotification_Invoked;
            AppNotificationManager.Default.Register();
        }
        catch
        {
        }
    }

    private static void App_Activated(object? sender, AppActivationArguments args)
    {
        NavigateExistingWindowHome();
    }

    private static void AppNotification_Invoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        NavigateExistingWindowHome();
    }

    private static void NavigateExistingWindowHome()
    {
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            MainWindow.NavigateToHome();
            MainWindow.Activate();
        });
    }

    private static void ConfigureServices()
    {
        SettingsService = new AppSettingsService();
        DatabaseService = new DatabaseService();
        ToolchainService = new ToolchainService();
        IconService = new IconService();
        FaviconService = new FaviconService(IconService, SettingsService);
        BuildService = new BuildService(ToolchainService, IconService, FaviconService, DatabaseService, SettingsService);
        HomeViewModel = new HomeViewModel();
    }
}
