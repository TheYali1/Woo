using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Woo_.Models;

namespace Woo_.ViewModels;

public sealed class BuildLogViewModel : ObservableObject
{
    private readonly DispatcherTimer _elapsedTimer = new();
    private CancellationTokenSource? _buildCts;
    private DateTime _buildStartedAt;
    private bool _hasStarted;
    private bool _isBuilding;
    private string _buildStatusText = "Ready";
    private string _elapsedTimeText = "00:00";
    private string _notificationMessage = string.Empty;
    private bool _isNotificationOpen;
    private string _outputFolderPath = string.Empty;

    public BuildLogViewModel()
    {
        StopBuildCommand = new RelayCommand(StopBuild, () => IsBuilding);
        CopyLogCommand = new RelayCommand(CopyLog, () => BuildLogEntries.Count > 0);
        ClearLogCommand = new RelayCommand(ClearLog, () => !IsBuilding && BuildLogEntries.Count > 0);

        _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        _elapsedTimer.Tick += (_, _) => UpdateElapsedTime();
    }

    public ObservableCollection<BuildLogEntry> BuildLogEntries { get; } = [];
    public IRelayCommand StopBuildCommand { get; }
    public IRelayCommand CopyLogCommand { get; }
    public IRelayCommand ClearLogCommand { get; }
    public BuildRequest? CurrentRequest { get; private set; }

    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (SetProperty(ref _isBuilding, value))
            {
                StopBuildCommand.NotifyCanExecuteChanged();
                ClearLogCommand.NotifyCanExecuteChanged();
            }
        }
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

    public async Task StartAsync(BuildRequest request)
    {
        if (_hasStarted)
        {
            return;
        }

        _hasStarted = true;
        CurrentRequest = request;
        BuildLogEntries.Clear();
        CopyLogCommand.NotifyCanExecuteChanged();
        ClearLogCommand.NotifyCanExecuteChanged();
        IsNotificationOpen = false;
        BuildStatusText = "Building...";
        ElapsedTimeText = "00:00";
        IsBuilding = true;
        _buildStartedAt = DateTime.Now;
        _buildCts = new CancellationTokenSource();
        _elapsedTimer.Start();

        var progress = new Progress<BuildLogEntry>(entry =>
        {
            BuildLogEntries.Add(entry);
            CopyLogCommand.NotifyCanExecuteChanged();
        });

        try
        {
            var result = await App.BuildService.BuildAsync(
                request.Configuration,
                request.ConflictChoice,
                progress,
                _buildCts.Token);

            BuildStatusText = result.Success ? "Success" : result.Canceled ? "Canceled" : "Failed";
            OutputFolderPath = result.Success ? result.ProjectDirectory : string.Empty;
            NotificationMessage = result.Message;
            IsNotificationOpen = !string.IsNullOrWhiteSpace(result.Message);
        }
        finally
        {
            IsBuilding = false;
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

    public string GetLogText()
    {
        return string.Join(Environment.NewLine, BuildLogEntries.Select(entry => entry.Text));
    }

    private void CopyLog()
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(GetLogText());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void ClearLog()
    {
        BuildLogEntries.Clear();
        CopyLogCommand.NotifyCanExecuteChanged();
        ClearLogCommand.NotifyCanExecuteChanged();
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
