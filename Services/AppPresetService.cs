using System.Text.Json;
using Woo_.Models;

namespace Woo_.Services;

public static class AppPresetService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task ExportAsync(string path, BuildConfiguration configuration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var preset = new WooAppPreset
        {
            Configuration = configuration
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(preset, SerializerOptions));
    }

    public static async Task<BuildConfiguration> ImportAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        var preset = JsonSerializer.Deserialize<WooAppPreset>(json, SerializerOptions);
        if (preset?.Configuration is null)
        {
            throw new InvalidDataException("This .wooapp file does not contain valid Woo! app settings.");
        }

        return preset.Configuration;
    }

    private sealed class WooAppPreset
    {
        public string Format { get; set; } = "wooapp";
        public int Version { get; set; } = 1;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public BuildConfiguration? Configuration { get; set; }
    }
}
