using System.Text.Json;
using Woo_.Helpers;
using Woo_.Models;

namespace Woo_.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public AppSettingsService()
    {
        AppDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Woo!");
        SettingsPath = Path.Combine(AppDataDirectory, "settings.json");
        Settings = Load();
    }

    public string AppDataDirectory { get; }
    public string SettingsPath { get; }
    public AppSettings Settings { get; private set; }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, SerializerOptions));
    }

    public async Task ExportAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Settings, SerializerOptions));
    }

    public async Task ImportAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        Settings = Normalize(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
        Save();
    }

    public void Reset()
    {
        Settings = new AppSettings();
        Save();
    }

    private AppSettings Load()
    {
        Directory.CreateDirectory(AppDataDirectory);

        if (!File.Exists(SettingsPath))
        {
            var defaults = new AppSettings();
            Settings = defaults;
            Save();
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return Normalize(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        if (settings.SettingsSchemaVersion < 2)
        {
            settings.NewLinkRedirectByDefault = true;
            settings.SettingsSchemaVersion = 2;
        }

        if (settings.SettingsSchemaVersion < 3)
        {
            settings.CheckUpdatesOnStartup = true;
            settings.SettingsSchemaVersion = 3;
        }

        if (settings.SettingsSchemaVersion < 4)
        {
            settings.CustomScriptsByDefault = false;
            settings.DefaultCustomScriptCode = string.Empty;
            settings.SettingsSchemaVersion = 4;
        }

        if (settings.SettingsSchemaVersion < 5)
        {
            settings.IncludeInstallerByDefault = false;
            settings.SettingsSchemaVersion = 5;
        }

        if (FileSystemHelper.TryNormalizeDirectoryPath(settings.DefaultOutputDirectory, out var outputDirectory, out _))
        {
            settings.DefaultOutputDirectory = outputDirectory;
        }

        return settings;
    }
}
