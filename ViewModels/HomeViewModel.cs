using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Woo_.Helpers;
using Woo_.Models;
using Woo_.Services;

namespace Woo_.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly DispatcherTimer _elapsedTimer = new();
    private DateTime _buildStartedAt;
    private string _websiteUrl = string.Empty;
    private int _sourceKindIndex;
    private string _localSourcePath = string.Empty;
    private string _appName = string.Empty;
    private int _frameworkIndex;
    private string _outputDirectory = string.Empty;
    private bool _autoFetchIcon = true;
    private string? _customIconPath;
    private string? _resolvedIconPath;
    private BitmapImage? _iconPreviewSource;
    private bool _includeAdBlocker;
    private bool _singleExecutable;
    private bool _includeInstaller;
    private bool _electronIncludeAdBlocker;
    private bool _electronSingleExecutable;
    private bool _electronIncludeInstaller;
    private double _windowWidth = 1280;
    private double _windowHeight = 800;
    private bool _allowResizing = true;
    private bool _startMaximized;
    private bool _showMenuBar;
    private bool _enableDevTools;
    private bool _newLinkRedirect;
    private bool _persistCookies = true;
    private bool _mouseNavigation;
    private bool _restrictToMainUrl;
    private bool _disableCaching;
    private bool _allowDownloads = true;
    private bool _electronAllowDownloads = true;
    private bool _systemTray;
    private bool _electronSystemTray;
    private bool _customScriptsEnabled;
    private bool _electronCustomScriptsEnabled;
    private string _customScriptCode = string.Empty;
    private string _customScriptValidationMessage = string.Empty;
    private bool _customScriptValidationSuccess = true;
    private string _userAgentOverride = string.Empty;
    private string _validationMessage = string.Empty;
    private bool _hasValidationError;
    private bool _isUrlValid;
    private bool _isFetchingAppName;
    private bool _isFetchingIcon;
    private bool _isBuilding;
    private bool _buildLogOpen;
    private CancellationTokenSource? _buildCts;
    private BuildRequest? _currentRequest;
    private string _outputFolderPath = string.Empty;
    private double _buildProgress;
    private string _buildStatusText = "Idle";
    private string _elapsedTimeText = "00:00";
    private string _notificationMessage = string.Empty;
    private bool _isNotificationOpen;
    private bool _isNotificationSuccess;
    private string _appNameFetchMessage = string.Empty;
    private string _iconFetchMessage = string.Empty;
    private CancellationTokenSource? _appNameMessageCts;
    private CancellationTokenSource? _iconMessageCts;

    public HomeViewModel()
    {
        var settings = App.SettingsService.Settings;
        _outputDirectory = FileSystemHelper.NormalizeDirectoryPath(settings.DefaultOutputDirectory);
        _frameworkIndex = settings.DefaultFramework.Equals("Tauri", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _includeAdBlocker = settings.IncludeAdBlockerByDefault;
        _singleExecutable = settings.SingleExeByDefault;
        _includeInstaller = settings.IncludeInstallerByDefault && !settings.SingleExeByDefault;
        _electronIncludeAdBlocker = _includeAdBlocker;
        _electronSingleExecutable = _singleExecutable;
        _electronIncludeInstaller = _includeInstaller;
        _enableDevTools = settings.EnableDevToolsByDefault;
        _allowResizing = settings.AllowResizingByDefault;
        _startMaximized = settings.StartMaximizedByDefault;
        _showMenuBar = settings.ShowMenuBarByDefault;
        _newLinkRedirect = settings.NewLinkRedirectByDefault;
        _persistCookies = settings.PersistCookiesByDefault;
        _mouseNavigation = settings.MouseNavigationByDefault;
        _restrictToMainUrl = settings.RestrictToMainUrlByDefault;
        _disableCaching = settings.DisableCachingByDefault;
        _electronAllowDownloads = _allowDownloads;
        _systemTray = settings.SystemTrayByDefault;
        _electronSystemTray = _systemTray;
        _customScriptCode = settings.DefaultCustomScriptCode;
        _electronCustomScriptsEnabled = settings.CustomScriptsByDefault;
        _customScriptsEnabled = _frameworkIndex == 0 && settings.CustomScriptsByDefault;
        _userAgentOverride = settings.DefaultUserAgentOverride;
        _windowWidth = settings.DefaultWindowWidth;
        _windowHeight = settings.DefaultWindowHeight;
        _autoFetchIcon = false;
        ApplyFrameworkConstraints();

        StopBuildCommand = new RelayCommand(StopBuild, () => IsBuilding);
        ClearLogCommand = new RelayCommand(ClearLog, () => !IsBuilding && BuildLogEntries.Count > 0);
        CopyLogCommand = new RelayCommand(CopyLog, () => BuildLogEntries.Count > 0);
        FetchAppNameCommand = new AsyncRelayCommand(FetchAppNameAsync, () => IsUrlValid && !IsFetchingAppName);
        FetchIconCommand = new AsyncRelayCommand(FetchIconAsync, () => IsUrlValid && !IsFetchingIcon);

        _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        _elapsedTimer.Tick += (_, _) => UpdateElapsedTime();

        SetIconPreview(Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.png"));
    }

    public ObservableCollection<BuildLogEntry> BuildLogEntries { get; } = [];
    public IRelayCommand StopBuildCommand { get; }
    public IRelayCommand ClearLogCommand { get; }
    public IRelayCommand CopyLogCommand { get; }
    public IAsyncRelayCommand FetchAppNameCommand { get; }
    public IAsyncRelayCommand FetchIconCommand { get; }

    public string WebsiteUrl
    {
        get => _websiteUrl;
        set
        {
            if (SetProperty(ref _websiteUrl, value))
            {
                ResetFetchedMetadataForUrlChange();
                ValidateUrl();
                OnPropertyChanged(nameof(CanBuild));
            }
        }
    }

    public int SourceKindIndex
    {
        get => _sourceKindIndex;
        set
        {
            if (SetProperty(ref _sourceKindIndex, value))
            {
                ValidateUrl();
                OnPropertyChanged(nameof(IsWebsiteSource));
                OnPropertyChanged(nameof(IsLocalSource));
                OnPropertyChanged(nameof(LocalSourcePickerLabel));
                OnPropertyChanged(nameof(LocalSourcePlaceholder));
                OnPropertyChanged(nameof(CanBuild));
            }
        }
    }

    public bool IsWebsiteSource => SourceKindIndex == 0;
    public bool IsLocalSource => SourceKindIndex == 1;
    public string LocalSourcePickerLabel => "Choose HTML File";
    public string LocalSourcePlaceholder => "Select an .html file";

    public string LocalSourcePath
    {
        get => _localSourcePath;
        set
        {
            if (SetProperty(ref _localSourcePath, value))
            {
                ValidateUrl();
                OnPropertyChanged(nameof(CanBuild));
            }
        }
    }

    public string AppName
    {
        get => _appName;
        set
        {
            if (SetProperty(ref _appName, value))
            {
                OnPropertyChanged(nameof(CanBuild));
            }
        }
    }

    public int FrameworkIndex
    {
        get => _frameworkIndex;
        set
        {
            var wasElectron = IsElectronSelected;
            if (wasElectron)
            {
                _electronIncludeAdBlocker = _includeAdBlocker;
                _electronSingleExecutable = _singleExecutable;
                _electronAllowDownloads = _allowDownloads;
                _electronSystemTray = _systemTray;
                _electronCustomScriptsEnabled = _customScriptsEnabled;
            }

            if (SetProperty(ref _frameworkIndex, value))
            {
                ApplyFrameworkConstraints();
                OnPropertyChanged(nameof(IsElectronSelected));
            }
        }
    }

    public bool IsElectronSelected => FrameworkIndex == 0;

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (SetProperty(ref _outputDirectory, value))
            {
                OnPropertyChanged(nameof(CanBuild));
            }
        }
    }

    public bool AutoFetchIcon
    {
        get => _autoFetchIcon;
        private set => SetProperty(ref _autoFetchIcon, value);
    }

    public string? CustomIconPath
    {
        get => _customIconPath;
        set => SetProperty(ref _customIconPath, value);
    }

    public BitmapImage? IconPreviewSource
    {
        get => _iconPreviewSource;
        private set => SetProperty(ref _iconPreviewSource, value);
    }

    public bool IncludeAdBlocker
    {
        get => _includeAdBlocker;
        set
        {
            if (!IsElectronSelected)
            {
                SetProperty(ref _includeAdBlocker, false);
                return;
            }

            _electronIncludeAdBlocker = value;
            SetProperty(ref _includeAdBlocker, value);
        }
    }

    public bool SingleExecutable
    {
        get => _singleExecutable;
        set
        {
            if (!IsElectronSelected)
            {
                SetProperty(ref _singleExecutable, false);
                return;
            }

            _electronSingleExecutable = value;
            if (SetProperty(ref _singleExecutable, value) && value)
            {
                _electronIncludeInstaller = false;
                SetProperty(ref _includeInstaller, false, nameof(IncludeInstaller));
            }
        }
    }

    public bool IncludeInstaller
    {
        get => _includeInstaller;
        set
        {
            _electronIncludeInstaller = value;
            if (SetProperty(ref _includeInstaller, value) && value)
            {
                _electronSingleExecutable = false;
                SetProperty(ref _singleExecutable, false, nameof(SingleExecutable));
            }
        }
    }

    public double WindowWidth
    {
        get => _windowWidth;
        set => SetProperty(ref _windowWidth, value);
    }

    public double WindowHeight
    {
        get => _windowHeight;
        set => SetProperty(ref _windowHeight, value);
    }

    public bool AllowResizing
    {
        get => _allowResizing;
        set => SetProperty(ref _allowResizing, value);
    }

    public bool StartMaximized
    {
        get => _startMaximized;
        set => SetProperty(ref _startMaximized, value);
    }

    public bool ShowMenuBar
    {
        get => _showMenuBar;
        set => SetProperty(ref _showMenuBar, value);
    }

    public bool EnableDevTools
    {
        get => _enableDevTools;
        set => SetProperty(ref _enableDevTools, value);
    }

    public bool NewLinkRedirect
    {
        get => _newLinkRedirect;
        set => SetProperty(ref _newLinkRedirect, value);
    }

    public bool PersistCookies
    {
        get => _persistCookies;
        set => SetProperty(ref _persistCookies, value);
    }

    public bool MouseNavigation
    {
        get => _mouseNavigation;
        set => SetProperty(ref _mouseNavigation, value);
    }

    public bool RestrictToMainUrl
    {
        get => _restrictToMainUrl;
        set => SetProperty(ref _restrictToMainUrl, value);
    }

    public bool DisableCaching
    {
        get => _disableCaching;
        set => SetProperty(ref _disableCaching, value);
    }

    public bool AllowDownloads
    {
        get => _allowDownloads;
        set
        {
            if (!IsElectronSelected)
            {
                SetProperty(ref _allowDownloads, false);
                return;
            }

            _electronAllowDownloads = value;
            SetProperty(ref _allowDownloads, value);
        }
    }

    public bool SystemTray
    {
        get => _systemTray;
        set
        {
            if (!IsElectronSelected)
            {
                SetProperty(ref _systemTray, false);
                return;
            }

            _electronSystemTray = value;
            SetProperty(ref _systemTray, value);
        }
    }

    public bool CustomScriptsEnabled
    {
        get => _customScriptsEnabled;
        set
        {
            if (!IsElectronSelected)
            {
                SetProperty(ref _customScriptsEnabled, false);
                return;
            }

            _electronCustomScriptsEnabled = value;
            if (SetProperty(ref _customScriptsEnabled, value) &&
                value &&
                string.IsNullOrWhiteSpace(CustomScriptCode) &&
                !string.IsNullOrWhiteSpace(App.SettingsService.Settings.DefaultCustomScriptCode))
            {
                CustomScriptCode = App.SettingsService.Settings.DefaultCustomScriptCode;
            }
        }
    }

    public string CustomScriptCode
    {
        get => _customScriptCode;
        set
        {
            if (SetProperty(ref _customScriptCode, value))
            {
                OnPropertyChanged(nameof(CustomScriptCharacterCountText));
            }
        }
    }

    public string CustomScriptCharacterCountText => $"{CustomScriptCode.Length:N0} chars";

    public string CustomScriptValidationMessage
    {
        get => _customScriptValidationMessage;
        private set
        {
            if (SetProperty(ref _customScriptValidationMessage, value))
            {
                OnPropertyChanged(nameof(HasCustomScriptValidationMessage));
            }
        }
    }

    public bool HasCustomScriptValidationMessage => !string.IsNullOrWhiteSpace(CustomScriptValidationMessage);

    public bool CustomScriptValidationSuccess
    {
        get => _customScriptValidationSuccess;
        private set => SetProperty(ref _customScriptValidationSuccess, value);
    }

    public string UserAgentOverride
    {
        get => _userAgentOverride;
        set => SetProperty(ref _userAgentOverride, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public bool HasValidationError
    {
        get => _hasValidationError;
        private set => SetProperty(ref _hasValidationError, value);
    }

    public bool IsUrlValid
    {
        get => _isUrlValid;
        private set
        {
            if (SetProperty(ref _isUrlValid, value))
            {
                FetchAppNameCommand.NotifyCanExecuteChanged();
                FetchIconCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsFetchingAppName
    {
        get => _isFetchingAppName;
        private set
        {
            if (SetProperty(ref _isFetchingAppName, value))
            {
                FetchAppNameCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsFetchingIcon
    {
        get => _isFetchingIcon;
        private set
        {
            if (SetProperty(ref _isFetchingIcon, value))
            {
                FetchIconCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (SetProperty(ref _isBuilding, value))
            {
                StopBuildCommand.NotifyCanExecuteChanged();
                ClearLogCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanBuild));
            }
        }
    }

    public bool BuildLogOpen
    {
        get => _buildLogOpen;
        set => SetProperty(ref _buildLogOpen, value);
    }

    public BuildRequest? CurrentRequest
    {
        get => _currentRequest;
        private set => SetProperty(ref _currentRequest, value);
    }

    public string OutputFolderPath
    {
        get => _outputFolderPath;
        private set
        {
            if (SetProperty(ref _outputFolderPath, value))
            {
                OnPropertyChanged(nameof(CanOpenOutputFolder));
            }
        }
    }

    public bool CanOpenOutputFolder => !string.IsNullOrWhiteSpace(OutputFolderPath) && Directory.Exists(OutputFolderPath);

    public double BuildProgress
    {
        get => _buildProgress;
        private set => SetProperty(ref _buildProgress, value);
    }

    public string BuildStatusText
    {
        get => _buildStatusText;
        private set => SetProperty(ref _buildStatusText, value);
    }

    public string ElapsedTimeText
    {
        get => _elapsedTimeText;
        private set => SetProperty(ref _elapsedTimeText, value);
    }

    public string NotificationMessage
    {
        get => _notificationMessage;
        private set => SetProperty(ref _notificationMessage, value);
    }

    public bool IsNotificationOpen
    {
        get => _isNotificationOpen;
        set => SetProperty(ref _isNotificationOpen, value);
    }

    public bool IsNotificationSuccess
    {
        get => _isNotificationSuccess;
        private set => SetProperty(ref _isNotificationSuccess, value);
    }

    public string AppNameFetchMessage
    {
        get => _appNameFetchMessage;
        private set
        {
            if (SetProperty(ref _appNameFetchMessage, value))
            {
                OnPropertyChanged(nameof(HasAppNameFetchMessage));
            }
        }
    }

    public bool HasAppNameFetchMessage => !string.IsNullOrWhiteSpace(AppNameFetchMessage);

    public string IconFetchMessage
    {
        get => _iconFetchMessage;
        private set
        {
            if (SetProperty(ref _iconFetchMessage, value))
            {
                OnPropertyChanged(nameof(HasIconFetchMessage));
            }
        }
    }

    public bool HasIconFetchMessage => !string.IsNullOrWhiteSpace(IconFetchMessage);

    public bool CanBuild =>
        !IsBuilding &&
        IsSourceValid(out _) &&
        !string.IsNullOrWhiteSpace(OutputDirectory);

    public BuildConfiguration CreateConfiguration()
    {
        return new BuildConfiguration
        {
            WebsiteUrl = WebsiteUrl,
            SourceKind = SourceKindIndex switch
            {
                1 => AppSourceKind.HtmlFile,
                _ => AppSourceKind.Website
            },
            LocalSourcePath = LocalSourcePath,
            AppName = AppName,
            Framework = FrameworkIndex == 1 ? OutputFramework.Tauri : OutputFramework.Electron,
            OutputDirectory = FileSystemHelper.TryNormalizeDirectoryPath(OutputDirectory, out var normalizedOutputDirectory, out _)
                ? normalizedOutputDirectory
                : OutputDirectory.Trim(),
            AutoFetchIcon = AutoFetchIcon,
            CustomIconPath = CustomIconPath,
            ResolvedIconPath = _resolvedIconPath,
            IncludeAdBlocker = IsElectronSelected && IncludeAdBlocker,
            SingleExecutable = IsElectronSelected && SingleExecutable,
            IncludeInstaller = IncludeInstaller,
            WindowWidth = Math.Max(320, (int)WindowWidth),
            WindowHeight = Math.Max(240, (int)WindowHeight),
            AllowResizing = AllowResizing,
            StartMaximized = StartMaximized,
            ShowMenuBar = ShowMenuBar,
            EnableDevTools = EnableDevTools,
            NewLinkRedirect = NewLinkRedirect,
            PersistCookies = PersistCookies,
            MouseNavigation = MouseNavigation,
            RestrictToMainUrl = RestrictToMainUrl,
            DisableCaching = DisableCaching,
            AllowDownloads = IsElectronSelected && AllowDownloads,
            SystemTray = IsElectronSelected && SystemTray,
            CustomScriptsEnabled = IsElectronSelected && CustomScriptsEnabled,
            CustomScriptCode = CustomScriptCode,
            CreateDesktopShortcutAfterBuild = App.SettingsService.Settings.CreateDesktopShortcutAfterBuild,
            OpenAppAfterBuild = App.SettingsService.Settings.OpenAppAfterBuild,
            OpenFolderAfterBuild = App.SettingsService.Settings.OpenFolderAfterBuild,
            ShowBuildCompleteNotification = App.SettingsService.Settings.ShowBuildCompleteNotification,
            UserAgentOverride = UserAgentOverride,
            IconUrl = IconUrl
        };
    }

    public Task BuildAsync(OutputConflictChoice conflictChoice)
    {
        return BuildAsync(new BuildRequest(CreateConfiguration(), conflictChoice));
    }

    public async Task BuildAsync(BuildRequest request)
    {
        if (IsBuilding)
        {
            return;
        }

        CurrentRequest = request;
        BuildLogEntries.Clear();
        CopyLogCommand.NotifyCanExecuteChanged();
        ClearLogCommand.NotifyCanExecuteChanged();
        BuildLogOpen = true;
        IsBuilding = true;
        BuildStatusText = "Building...";
        ElapsedTimeText = "00:00";
        BuildProgress = 0;
        IsNotificationOpen = false;
        OutputFolderPath = string.Empty;
        _buildCts = new CancellationTokenSource();
        _buildStartedAt = DateTime.Now;
        _elapsedTimer.Start();

        var progress = new Progress<BuildLogEntry>(entry =>
        {
            BuildLogEntries.Add(entry);
            UpdateBuildProgress(entry.Text);
            CopyLogCommand.NotifyCanExecuteChanged();
            ClearLogCommand.NotifyCanExecuteChanged();
        });

        try
        {
            var result = await App.BuildService.BuildAsync(request.Configuration, request.ConflictChoice, progress, _buildCts.Token);
            BuildStatusText = result.Success ? "Success" : result.Canceled ? "Canceled" : "Failed";
            BuildProgress = result.Success ? 100 : BuildProgress;
            OutputFolderPath = result.Success ? result.ProjectDirectory : string.Empty;
            IsNotificationSuccess = result.Success;
            NotificationMessage = result.Message;
            IsNotificationOpen = true;
        }
        finally
        {
            IsBuilding = false;
            _buildCts?.Dispose();
            _buildCts = null;
            _elapsedTimer.Stop();
            UpdateElapsedTime();
        }
    }

    public void StopBuild()
    {
        if (!IsBuilding)
        {
            return;
        }

        BuildStatusText = "Stopping...";
        _buildCts?.Cancel();
    }

    public void UseCurrentUserAgent()
    {
        UserAgentOverride = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36 Edg/126.0";
    }

    public async Task FetchAppNameAsync()
    {
        ValidateUrl();
        if (!IsUrlValid)
        {
            return;
        }

        IsFetchingAppName = true;
        AppNameFetchMessage = string.Empty;
        try
        {
            var metadata = await App.FaviconService.FetchTitleAsync(WebsiteUrl);
            if (!string.IsNullOrWhiteSpace(metadata.NormalizedUrl))
            {
                WebsiteUrl = metadata.NormalizedUrl;
            }

            if (metadata.TitleFetchedFromWebsite)
            {
                AppName = metadata.SuggestedAppName;
                SetTemporaryAppNameFetchMessage("Website title loaded.");
            }
            else
            {
                SetTemporaryAppNameFetchMessage(metadata.Error ?? "Could not fetch the website title.");
            }
        }
        finally
        {
            IsFetchingAppName = false;
        }
    }

    public async Task FetchIconAsync()
    {
        ValidateUrl();
        if (!IsUrlValid)
        {
            return;
        }

        IsFetchingIcon = true;
        IconFetchMessage = string.Empty;
        try
        {
            var metadata = await App.FaviconService.FetchIconAsync(WebsiteUrl);
            if (!string.IsNullOrWhiteSpace(metadata.NormalizedUrl))
            {
                WebsiteUrl = metadata.NormalizedUrl;
            }

            if (metadata.IconFetchedFromWebsite && !string.IsNullOrWhiteSpace(metadata.IconPath))
            {
                _resolvedIconPath = metadata.IconPath;
                IconUrl = metadata.IconUrl;
                CustomIconPath = null;
                AutoFetchIcon = true;
                SetIconPreview(metadata.PreviewPngPath ?? metadata.IconPath);
                SetTemporaryIconFetchMessage("Website icon loaded.");
            }
            else
            {
                SetTemporaryIconFetchMessage(metadata.Error ?? "Could not fetch the website icon.");
            }
        }
        finally
        {
            IsFetchingIcon = false;
        }
    }

    public void SetCustomIcon(string path)
    {
        CustomIconPath = path;
        AutoFetchIcon = false;
        _resolvedIconPath = path;
        IconUrl = null;
        SetTemporaryIconFetchMessage("Custom icon selected.");
        SetIconPreview(path);
    }

    public void LoadConfiguration(BuildConfiguration configuration)
    {
        WebsiteUrl = configuration.WebsiteUrl;
        SourceKindIndex = configuration.SourceKind switch
        {
            AppSourceKind.HtmlFile => 1,
            _ => 0
        };
        LocalSourcePath = configuration.LocalSourcePath;
        AppName = configuration.AppName;
        FrameworkIndex = configuration.Framework == OutputFramework.Tauri ? 1 : 0;
        OutputDirectory = configuration.OutputDirectory;
        AutoFetchIcon = configuration.AutoFetchIcon;
        CustomIconPath = configuration.CustomIconPath;
        _resolvedIconPath = configuration.ResolvedIconPath;
        IconUrl = configuration.IconUrl;
        _electronIncludeAdBlocker = configuration.IncludeAdBlocker;
        _electronSingleExecutable = configuration.SingleExecutable;
        _electronIncludeInstaller = configuration.IncludeInstaller;
        IncludeAdBlocker = configuration.IncludeAdBlocker;
        SingleExecutable = configuration.SingleExecutable;
        IncludeInstaller = configuration.IncludeInstaller;
        WindowWidth = configuration.WindowWidth;
        WindowHeight = configuration.WindowHeight;
        AllowResizing = configuration.AllowResizing;
        StartMaximized = configuration.StartMaximized;
        ShowMenuBar = configuration.ShowMenuBar;
        EnableDevTools = configuration.EnableDevTools;
        NewLinkRedirect = configuration.NewLinkRedirect;
        PersistCookies = configuration.PersistCookies;
        MouseNavigation = configuration.MouseNavigation;
        RestrictToMainUrl = configuration.RestrictToMainUrl;
        DisableCaching = configuration.DisableCaching;
        AllowDownloads = configuration.AllowDownloads;
        _electronSystemTray = configuration.SystemTray;
        SystemTray = configuration.SystemTray;
        _electronCustomScriptsEnabled = configuration.CustomScriptsEnabled;
        CustomScriptsEnabled = configuration.CustomScriptsEnabled;
        CustomScriptCode = configuration.CustomScriptCode;
        ClearCustomScriptValidationMessage();
        UserAgentOverride = configuration.UserAgentOverride ?? string.Empty;
        ApplyFrameworkConstraints();

        if (!string.IsNullOrWhiteSpace(_resolvedIconPath))
        {
            SetIconPreview(_resolvedIconPath);
        }
    }

    public void ResetConfigurationToDefaults()
    {
        var defaults = new AppSettings();
        WebsiteUrl = string.Empty;
        SourceKindIndex = 0;
        LocalSourcePath = string.Empty;
        AppName = string.Empty;
        FrameworkIndex = defaults.DefaultFramework.Equals("Tauri", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        OutputDirectory = FileSystemHelper.NormalizeDirectoryPath(defaults.DefaultOutputDirectory);
        AutoFetchIcon = false;
        CustomIconPath = null;
        _resolvedIconPath = null;
        IconUrl = null;
        IncludeAdBlocker = defaults.IncludeAdBlockerByDefault;
        SingleExecutable = defaults.SingleExeByDefault;
        IncludeInstaller = defaults.IncludeInstallerByDefault && !defaults.SingleExeByDefault;
        WindowWidth = defaults.DefaultWindowWidth;
        WindowHeight = defaults.DefaultWindowHeight;
        AllowResizing = defaults.AllowResizingByDefault;
        StartMaximized = defaults.StartMaximizedByDefault;
        ShowMenuBar = defaults.ShowMenuBarByDefault;
        EnableDevTools = defaults.EnableDevToolsByDefault;
        NewLinkRedirect = defaults.NewLinkRedirectByDefault;
        PersistCookies = defaults.PersistCookiesByDefault;
        MouseNavigation = defaults.MouseNavigationByDefault;
        RestrictToMainUrl = defaults.RestrictToMainUrlByDefault;
        DisableCaching = defaults.DisableCachingByDefault;
        AllowDownloads = true;
        SystemTray = defaults.SystemTrayByDefault;
        CustomScriptsEnabled = defaults.CustomScriptsByDefault;
        CustomScriptCode = defaults.DefaultCustomScriptCode;
        ClearCustomScriptValidationMessage();
        UserAgentOverride = defaults.DefaultUserAgentOverride;
        ValidationMessage = string.Empty;
        HasValidationError = false;
        IsUrlValid = false;
        _electronIncludeAdBlocker = IncludeAdBlocker;
        _electronSingleExecutable = SingleExecutable;
        _electronIncludeInstaller = IncludeInstaller;
        _electronAllowDownloads = AllowDownloads;
        _electronSystemTray = SystemTray;
        _electronCustomScriptsEnabled = CustomScriptsEnabled;
        SetIconPreview(Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.png"));
        OnPropertyChanged(nameof(CanBuild));
    }

    public void ApplyDefaultCustomScriptIfNeeded()
    {
        if (CustomScriptsEnabled &&
            string.IsNullOrWhiteSpace(CustomScriptCode) &&
            !string.IsNullOrWhiteSpace(App.SettingsService.Settings.DefaultCustomScriptCode))
        {
            CustomScriptCode = App.SettingsService.Settings.DefaultCustomScriptCode;
        }
    }

    private string? IconUrl { get; set; }

    public void SetCustomScriptValidationResult(WooScriptValidationResult result)
    {
        CustomScriptValidationSuccess = result.Success;
        CustomScriptValidationMessage = result.Summary;
    }

    public void SetCustomScriptValidationMessage(string message, bool success)
    {
        CustomScriptValidationSuccess = success;
        CustomScriptValidationMessage = message;
    }

    public void ClearCustomScriptValidationMessage()
    {
        CustomScriptValidationMessage = string.Empty;
        CustomScriptValidationSuccess = true;
    }

    public string GetLogText()
    {
        return string.Join(Environment.NewLine, BuildLogEntries.Select(entry => entry.Text));
    }

    private void ClearLog()
    {
        BuildLogEntries.Clear();
        CopyLogCommand.NotifyCanExecuteChanged();
        ClearLogCommand.NotifyCanExecuteChanged();
    }

    private void CopyLog()
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(GetLogText());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void ApplyFrameworkConstraints()
    {
        if (!IsElectronSelected)
        {
            SetProperty(ref _includeAdBlocker, false, nameof(IncludeAdBlocker));
            SetProperty(ref _singleExecutable, false, nameof(SingleExecutable));
            SetProperty(ref _allowDownloads, false, nameof(AllowDownloads));
            SetProperty(ref _systemTray, false, nameof(SystemTray));
            SetProperty(ref _customScriptsEnabled, false, nameof(CustomScriptsEnabled));
            return;
        }

        SetProperty(ref _includeAdBlocker, _electronIncludeAdBlocker, nameof(IncludeAdBlocker));
        SetProperty(ref _includeInstaller, _electronIncludeInstaller, nameof(IncludeInstaller));
        SetProperty(ref _singleExecutable, _electronIncludeInstaller ? false : _electronSingleExecutable, nameof(SingleExecutable));
        SetProperty(ref _allowDownloads, _electronAllowDownloads, nameof(AllowDownloads));
        SetProperty(ref _systemTray, _electronSystemTray, nameof(SystemTray));
        SetProperty(ref _customScriptsEnabled, _electronCustomScriptsEnabled, nameof(CustomScriptsEnabled));
        if (_customScriptsEnabled &&
            string.IsNullOrWhiteSpace(CustomScriptCode) &&
            !string.IsNullOrWhiteSpace(App.SettingsService.Settings.DefaultCustomScriptCode))
        {
            CustomScriptCode = App.SettingsService.Settings.DefaultCustomScriptCode;
        }
    }

    private void ValidateUrl()
    {
        if (!IsWebsiteSource)
        {
            if (string.IsNullOrWhiteSpace(LocalSourcePath))
            {
                ValidationMessage = string.Empty;
                HasValidationError = false;
                IsUrlValid = false;
                return;
            }

            var sourceValid = IsSourceValid(out var sourceError);
            HasValidationError = !sourceValid;
            ValidationMessage = sourceValid ? string.Empty : sourceError;
            IsUrlValid = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(WebsiteUrl))
        {
            ValidationMessage = string.Empty;
            HasValidationError = false;
            IsUrlValid = false;
            return;
        }

        var valid = UrlHelper.TryNormalize(WebsiteUrl, out _, out var error);
        HasValidationError = !valid;
        ValidationMessage = valid ? string.Empty : error;
        IsUrlValid = valid;
    }

    private bool IsSourceValid(out string error)
    {
        error = string.Empty;
        if (IsWebsiteSource)
        {
            return UrlHelper.TryNormalize(WebsiteUrl, out _, out error);
        }

        if (string.IsNullOrWhiteSpace(LocalSourcePath))
        {
            error = "Choose an HTML file.";
            return false;
        }

        if (!File.Exists(LocalSourcePath))
        {
            error = "The selected source file does not exist.";
            return false;
        }

        var extension = Path.GetExtension(LocalSourcePath);
        if (!extension.Equals(".html", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            error = "Choose an .html file.";
            return false;
        }

        return true;
    }

    private void ResetFetchedMetadataForUrlChange()
    {
        AppNameFetchMessage = string.Empty;
        _iconMessageCts?.Cancel();
        IconFetchMessage = string.Empty;

        if (CustomIconPath is null)
        {
            _resolvedIconPath = null;
            IconUrl = null;
            AutoFetchIcon = false;
            SetIconPreview(Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.png"));
        }
    }

    private async void SetTemporaryAppNameFetchMessage(string message)
    {
        _appNameMessageCts?.Cancel();
        var cts = new CancellationTokenSource();
        _appNameMessageCts = cts;
        AppNameFetchMessage = message;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                AppNameFetchMessage = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void SetTemporaryIconFetchMessage(string message)
    {
        _iconMessageCts?.Cancel();
        var cts = new CancellationTokenSource();
        _iconMessageCts = cts;
        IconFetchMessage = message;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                IconFetchMessage = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetIconPreview(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                IconPreviewSource = new BitmapImage(new Uri(path));
            }
        }
        catch
        {
            IconPreviewSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Woo!.png")));
        }
    }

    private void UpdateBuildProgress(string line)
    {
        var next = BuildProgress;
        if (line.Contains("Starting Woo! build", StringComparison.OrdinalIgnoreCase))
        {
            next = 5;
        }
        else if (line.Contains("Project folder", StringComparison.OrdinalIgnoreCase))
        {
            next = 15;
        }
        else if (line.Contains("Icon resolved", StringComparison.OrdinalIgnoreCase))
        {
            next = 25;
        }
        else if (line.Contains("Scaffolding", StringComparison.OrdinalIgnoreCase))
        {
            next = 35;
        }
        else if (line.Contains("npm", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("install", StringComparison.OrdinalIgnoreCase))
        {
            next = Math.Max(next, 45);
        }
        else if (line.Contains("Compiling", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("electron-builder", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("tauri", StringComparison.OrdinalIgnoreCase))
        {
            next = Math.Max(next, 65);
        }
        else if (line.Contains("Built application at", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Finished", StringComparison.OrdinalIgnoreCase))
        {
            next = Math.Max(next, 90);
        }
        else if (line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase))
        {
            next = 100;
        }

        if (next > BuildProgress)
        {
            BuildProgress = next;
        }
    }

    private void UpdateElapsedTime()
    {
        if (_buildStartedAt == default)
        {
            ElapsedTimeText = "00:00";
            return;
        }

        var elapsed = DateTime.Now - _buildStartedAt;
        ElapsedTimeText = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }
}
