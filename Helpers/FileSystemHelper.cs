namespace Woo_.Helpers;

public static class FileSystemHelper
{
    public static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        expanded = expanded.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        var root = Path.GetPathRoot(expanded);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Path.GetFullPath(expanded);
        }

        var remainder = expanded[root.Length..];
        var parts = remainder
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();

        return parts.Length == 0
            ? root
            : Path.Combine([root, ..parts]);
    }

    public static bool TryNormalizeDirectoryPath(string path, out string normalizedPath, out string error)
    {
        try
        {
            normalizedPath = NormalizeDirectoryPath(path);
            error = string.Empty;
            return !string.IsNullOrWhiteSpace(normalizedPath);
        }
        catch (Exception ex)
        {
            normalizedPath = string.Empty;
            error = $"Output directory path is invalid: {ex.Message}";
            return false;
        }
    }

    public static string GetUniqueDirectory(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory))
        {
            return baseDirectory;
        }

        for (var index = 2; index < 10_000; index++)
        {
            var candidate = $"{baseDirectory} ({index})";
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return $"{baseDirectory} ({DateTime.Now:yyyyMMddHHmmss})";
    }
}
