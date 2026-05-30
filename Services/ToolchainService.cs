using System.Diagnostics;
using Woo_.Models;

namespace Woo_.Services;

public sealed class ToolchainService
{
    public async Task<IReadOnlyList<ToolchainProbeResult>> VerifyAsync(AppSettings settings)
    {
        return
        [
            await ProbeAsync("Node.js", settings.NodePath, "node", "--version"),
            await ProbeAsync("npm", settings.NpmPath, "npm", "--version"),
            await ProbeAsync("Rust/Cargo", settings.CargoPath, "cargo", "--version")
        ];
    }

    public async Task<IReadOnlyList<ToolchainProbeResult>> VerifyForBuildAsync(OutputFramework framework, AppSettings settings)
    {
        var results = new List<ToolchainProbeResult>
        {
            await ProbeAsync("Node.js", settings.NodePath, "node", "--version"),
            await ProbeAsync("npm", settings.NpmPath, "npm", "--version")
        };

        if (framework == OutputFramework.Tauri)
        {
            results.Add(await ProbeAsync("Rust/Cargo", settings.CargoPath, "cargo", "--version"));
        }

        return results;
    }

    public string? DetectPath(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            process.WaitForExit(3000);
            var first = process.StandardOutput.ReadLine();
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ToolchainProbeResult> ProbeAsync(string name, string? configuredPath, string fallbackExecutable, string arguments)
    {
        var executable = string.IsNullOrWhiteSpace(configuredPath) ? fallbackExecutable : configuredPath.Trim();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return ToolchainProbeResult.Missing(name, executable);
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var version = string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : stdout.Trim();

            return process.ExitCode == 0
                ? ToolchainProbeResult.Found(name, executable, version)
                : ToolchainProbeResult.Missing(name, executable, version);
        }
        catch (Exception ex)
        {
            return ToolchainProbeResult.Missing(name, executable, ex.Message);
        }
    }
}

public sealed class ToolchainProbeResult
{
    public string Name { get; init; } = string.Empty;
    public string Executable { get; init; } = string.Empty;
    public bool IsFound { get; init; }
    public string Details { get; init; } = string.Empty;
    public string DisplayText => IsFound
        ? $"{Name}: {Details}"
        : $"{Name}: missing ({Executable}) {Details}".Trim();

    public static ToolchainProbeResult Found(string name, string executable, string details)
    {
        return new ToolchainProbeResult
        {
            Name = name,
            Executable = executable,
            IsFound = true,
            Details = details
        };
    }

    public static ToolchainProbeResult Missing(string name, string executable, string details = "")
    {
        return new ToolchainProbeResult
        {
            Name = name,
            Executable = executable,
            IsFound = false,
            Details = details
        };
    }
}
