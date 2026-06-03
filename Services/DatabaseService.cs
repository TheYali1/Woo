using Microsoft.Data.Sqlite;
using Woo_.Models;

namespace Woo_.Services;

public sealed class DatabaseService
{
    private readonly string _databasePath;

    public DatabaseService()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Woo!");
        _databasePath = Path.Combine(appData, "history.db");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS exports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                app_name TEXT NOT NULL,
                url TEXT NOT NULL,
                framework TEXT NOT NULL,
                output_path TEXT NOT NULL,
                icon_path TEXT,
                icon_url TEXT,
                ad_blocker_enabled INTEGER NOT NULL DEFAULT 0,
                single_exe INTEGER NOT NULL DEFAULT 0,
                include_installer INTEGER NOT NULL DEFAULT 0,
                new_link_redirect INTEGER NOT NULL DEFAULT 0,
                allow_downloads INTEGER NOT NULL DEFAULT 1,
                custom_scripts_enabled INTEGER NOT NULL DEFAULT 0,
                custom_script_code TEXT,
                window_width INTEGER NOT NULL DEFAULT 1280,
                window_height INTEGER NOT NULL DEFAULT 800,
                build_duration_seconds INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                status TEXT NOT NULL,
                build_log TEXT
            );
            """;

        await command.ExecuteNonQueryAsync();
        await AddColumnIfMissingAsync(connection, "icon_url", "TEXT");
        await AddColumnIfMissingAsync(connection, "include_installer", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(connection, "new_link_redirect", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(connection, "allow_downloads", "INTEGER NOT NULL DEFAULT 1");
        await AddColumnIfMissingAsync(connection, "custom_scripts_enabled", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(connection, "custom_script_code", "TEXT");
        await AddColumnIfMissingAsync(connection, "build_duration_seconds", "INTEGER NOT NULL DEFAULT 0");
    }

    public async Task<IReadOnlyList<ExportRecord>> GetExportsAsync()
    {
        await InitializeAsync();
        var records = new List<ExportRecord>();

        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, app_name, url, framework, output_path, icon_path, icon_url,
                   ad_blocker_enabled, single_exe, include_installer, new_link_redirect, allow_downloads, custom_scripts_enabled, custom_script_code, window_width, window_height,
                   build_duration_seconds, created_at, status, build_log
            FROM exports
            ORDER BY datetime(created_at) DESC, id DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var outputPath = reader.GetString(4);
            var iconPath = reader.IsDBNull(5) ? null : reader.GetString(5);
            if ((string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)) &&
                File.Exists(Path.Combine(outputPath, "icon.png")))
            {
                iconPath = Path.Combine(outputPath, "icon.png");
            }

            records.Add(new ExportRecord
            {
                Id = reader.GetInt64(0),
                AppName = reader.GetString(1),
                Url = reader.GetString(2),
                Framework = reader.GetString(3),
                OutputPath = outputPath,
                IconPath = iconPath,
                IconUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                AdBlockerEnabled = reader.GetInt32(7) == 1,
                SingleExe = reader.GetInt32(8) == 1,
                IncludeInstaller = reader.GetInt32(9) == 1,
                NewLinkRedirect = reader.GetInt32(10) == 1,
                AllowDownloads = reader.GetInt32(11) == 1,
                CustomScriptsEnabled = reader.GetInt32(12) == 1,
                CustomScriptCode = reader.IsDBNull(13) ? null : reader.GetString(13),
                WindowWidth = reader.GetInt32(14),
                WindowHeight = reader.GetInt32(15),
                BuildDurationSeconds = reader.GetInt32(16),
                CreatedAt = DateTime.TryParse(reader.GetString(17), out var createdAt) ? createdAt : DateTime.Now,
                Status = reader.GetString(18),
                BuildLog = reader.IsDBNull(19) ? null : reader.GetString(19)
            });
        }

        return records;
    }

    public async Task AddExportAsync(ExportRecord record)
    {
        await InitializeAsync();
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO exports (
                app_name, url, framework, output_path, icon_path, icon_url,
                ad_blocker_enabled, single_exe, include_installer, new_link_redirect, allow_downloads, custom_scripts_enabled, custom_script_code, window_width, window_height,
                build_duration_seconds, created_at, status, build_log
            )
            VALUES (
                $app_name, $url, $framework, $output_path, $icon_path, $icon_url,
                $ad_blocker_enabled, $single_exe, $include_installer, $new_link_redirect, $allow_downloads, $custom_scripts_enabled, $custom_script_code, $window_width, $window_height,
                $build_duration_seconds, $created_at, $status, $build_log
            );
            """;

        command.Parameters.AddWithValue("$app_name", record.AppName);
        command.Parameters.AddWithValue("$url", record.Url);
        command.Parameters.AddWithValue("$framework", record.Framework);
        command.Parameters.AddWithValue("$output_path", record.OutputPath);
        command.Parameters.AddWithValue("$icon_path", (object?)record.IconPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$icon_url", (object?)record.IconUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$ad_blocker_enabled", record.AdBlockerEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$single_exe", record.SingleExe ? 1 : 0);
        command.Parameters.AddWithValue("$include_installer", record.IncludeInstaller ? 1 : 0);
        command.Parameters.AddWithValue("$new_link_redirect", record.NewLinkRedirect ? 1 : 0);
        command.Parameters.AddWithValue("$allow_downloads", record.AllowDownloads ? 1 : 0);
        command.Parameters.AddWithValue("$custom_scripts_enabled", record.CustomScriptsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$custom_script_code", (object?)record.CustomScriptCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$window_width", record.WindowWidth);
        command.Parameters.AddWithValue("$window_height", record.WindowHeight);
        command.Parameters.AddWithValue("$build_duration_seconds", record.BuildDurationSeconds);
        command.Parameters.AddWithValue("$created_at", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$build_log", (object?)record.BuildLog ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteExportAsync(long id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM exports WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ClearAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM exports;";
        await command.ExecuteNonQueryAsync();
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection($"Data Source={_databasePath}");
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, string columnName, string definition)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(exports);";

        await using (var reader = await pragma.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Contains(columnName))
        {
            return;
        }

        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE exports ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync();
    }
}
