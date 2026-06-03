using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Woo_.Models;

namespace Woo_.ViewModels;

public sealed class HistoryViewModel : ObservableObject
{
    private string _searchText = string.Empty;
    private int _frameworkFilterIndex;
    private int _statusFilterIndex;
    private int _sortIndex;
    private bool _isLoading;
    private ExportRecord? _selectedRecord;

    public HistoryViewModel()
    {
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        DeleteRecordCommand = new AsyncRelayCommand<ExportRecord>(DeleteRecordAsync);
        OpenFolderCommand = new RelayCommand<ExportRecord>(OpenFolder);
        CopyUrlCommand = new RelayCommand<ExportRecord>(CopyUrl);
        RebuildCommand = new RelayCommand<ExportRecord>(Rebuild);
    }

    public ObservableCollection<ExportRecord> AllRecords { get; } = [];
    public ObservableCollection<ExportRecord> FilteredRecords { get; } = [];
    public ObservableCollection<BuildLogEntry> SelectedBuildLogEntries { get; } = [];

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand<ExportRecord> DeleteRecordCommand { get; }
    public IRelayCommand<ExportRecord> OpenFolderCommand { get; }
    public IRelayCommand<ExportRecord> CopyUrlCommand { get; }
    public IRelayCommand<ExportRecord> RebuildCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public int FrameworkFilterIndex
    {
        get => _frameworkFilterIndex;
        set
        {
            if (SetProperty(ref _frameworkFilterIndex, value))
            {
                ApplyFilter();
            }
        }
    }

    public int StatusFilterIndex
    {
        get => _statusFilterIndex;
        set
        {
            if (SetProperty(ref _statusFilterIndex, value))
            {
                ApplyFilter();
            }
        }
    }

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (SetProperty(ref _sortIndex, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public ExportRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                UpdateSelectedBuildLogEntries();
                OnPropertyChanged(nameof(HasSelectedRecord));
            }
        }
    }

    public bool HasRecords => FilteredRecords.Count > 0;
    public bool HasAnyRecords => AllRecords.Count > 0;
    public bool HasSelectedRecord => SelectedRecord is not null;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            AllRecords.Clear();
            foreach (var record in await App.DatabaseService.GetExportsAsync())
            {
                AllRecords.Add(record);
            }

            if (SelectedRecord is not null && AllRecords.All(record => record.Id != SelectedRecord.Id))
            {
                SelectedRecord = null;
            }

            ApplyFilter();
            OnPropertyChanged(nameof(HasAnyRecords));
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ClearAllAsync()
    {
        await App.DatabaseService.ClearAsync();
        SelectedRecord = null;
        await LoadAsync();
    }

    public async Task ExportCsvAsync(string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("App Name,Website URL,Framework,Output Path,Icon URL,Icon Path,Created At,Duration,Status,Ad Blocker,Single exe,Installer,New Link Redirect,Custom Scripts,Custom Script Code");

        foreach (var record in FilteredRecords)
        {
            builder.AppendLine(string.Join(",",
                Csv(record.AppName),
                Csv(record.Url),
                Csv(record.Framework),
                Csv(record.OutputPath),
                Csv(record.IconUrl ?? string.Empty),
                Csv(record.IconPath ?? string.Empty),
                Csv(record.CreatedAt.ToString("O")),
                Csv(record.BuildDurationText),
                Csv(record.Status),
                record.AdBlockerEnabled ? "Yes" : "No",
                record.SingleExe ? "Yes" : "No",
                record.IncludeInstaller ? "Yes" : "No",
                record.NewLinkRedirect ? "Yes" : "No",
                record.CustomScriptsEnabled ? "Yes" : "No",
                Csv(record.CustomScriptCode ?? string.Empty)));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8);
    }

    public async Task DeleteRecordAsync(ExportRecord? record)
    {
        if (record is null)
        {
            return;
        }

        await App.DatabaseService.DeleteExportAsync(record.Id);
        AllRecords.Remove(record);
        if (SelectedRecord?.Id == record.Id)
        {
            SelectedRecord = null;
        }

        ApplyFilter();
        OnPropertyChanged(nameof(HasAnyRecords));
    }

    public void OpenFolder(ExportRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.OutputPath))
        {
            return;
        }

        var target = Directory.Exists(record.OutputPath)
            ? record.OutputPath
            : Path.GetDirectoryName(record.OutputPath);

        if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
    }

    public void CopyUrl(ExportRecord? record)
    {
        if (record is null)
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(record.Url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    public void Rebuild(ExportRecord? record)
    {
        if (record is null)
        {
            return;
        }

        var configuration = CreateConfiguration(record);

        App.MainWindow?.NavigateToHome(configuration);
    }

    public BuildConfiguration CreateConfiguration(ExportRecord record)
    {
        return new BuildConfiguration
        {
            WebsiteUrl = record.Url,
            SourceKind = InferSourceKind(record.Url),
            LocalSourcePath = InferSourceKind(record.Url) == AppSourceKind.Website ? string.Empty : record.Url,
            AppName = record.AppName,
            Framework = record.Framework.Equals("Tauri", StringComparison.OrdinalIgnoreCase)
                ? OutputFramework.Tauri
                : OutputFramework.Electron,
            OutputDirectory = Path.GetDirectoryName(record.OutputPath) ?? App.SettingsService.Settings.DefaultOutputDirectory,
            ResolvedIconPath = record.IconPath,
            IconUrl = record.IconUrl,
            IncludeAdBlocker = record.AdBlockerEnabled,
            SingleExecutable = record.SingleExe,
            IncludeInstaller = record.IncludeInstaller,
            NewLinkRedirect = record.NewLinkRedirect,
            AllowDownloads = record.AllowDownloads,
            CustomScriptsEnabled = record.CustomScriptsEnabled,
            CustomScriptCode = record.CustomScriptCode ?? string.Empty,
            WindowWidth = record.WindowWidth,
            WindowHeight = record.WindowHeight
        };
    }

    private static AppSourceKind InferSourceKind(string value)
    {
        var extension = Path.GetExtension(value);
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return AppSourceKind.HtmlFile;
        }

        return AppSourceKind.Website;
    }

    private void ApplyFilter()
    {
        FilteredRecords.Clear();
        IEnumerable<ExportRecord> filtered = string.IsNullOrWhiteSpace(SearchText)
            ? AllRecords
            : AllRecords.Where(record =>
                record.AppName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                record.Url.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        filtered = FrameworkFilterIndex switch
        {
            1 => filtered.Where(record => record.Framework.Equals("Electron", StringComparison.OrdinalIgnoreCase)),
            2 => filtered.Where(record => record.Framework.Equals("Tauri", StringComparison.OrdinalIgnoreCase)),
            _ => filtered
        };

        filtered = StatusFilterIndex switch
        {
            1 => filtered.Where(record => record.Status.Equals("Success", StringComparison.OrdinalIgnoreCase)),
            2 => filtered.Where(record => record.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)),
            _ => filtered
        };

        filtered = SortIndex switch
        {
            1 => filtered.OrderBy(record => record.CreatedAt),
            2 => filtered.OrderBy(record => record.AppName),
            3 => filtered.OrderBy(record => record.Framework),
            4 => filtered.OrderBy(record => record.Status),
            _ => filtered.OrderByDescending(record => record.CreatedAt)
        };

        foreach (var record in filtered)
        {
            FilteredRecords.Add(record);
        }

        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(HasAnyRecords));
    }

    private void UpdateSelectedBuildLogEntries()
    {
        SelectedBuildLogEntries.Clear();

        if (string.IsNullOrWhiteSpace(SelectedRecord?.BuildLog))
        {
            return;
        }

        foreach (var line in SelectedRecord.BuildLog.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            SelectedBuildLogEntries.Add(new BuildLogEntry(line, ClassifyLogLine(line)));
        }
    }

    private static string ClassifyLogLine(string line)
    {
        if (line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Build success", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Built application at", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Finished", StringComparison.OrdinalIgnoreCase))
        {
            return "Green";
        }

        if (line.Contains("Build failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Red";
        }

        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Build canceled", StringComparison.OrdinalIgnoreCase))
        {
            return "Yellow";
        }

        return line.StartsWith('>')
            ? "Cyan"
            : "White";
    }

    private static string Csv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
