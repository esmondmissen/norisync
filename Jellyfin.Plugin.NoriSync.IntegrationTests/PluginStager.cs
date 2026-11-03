using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.NoriSync.IntegrationTests;

// Publishes the plugin and stages it into a directory layout matching the
// production zip (Jenkinsfile's stage/): loose DLLs plus a camelCase meta.json
// for Jellyfin's PluginManager to discover. One build serves every supported
// server line, so the same staged output is used whatever JELLYFIN_IMAGE says.
internal static class PluginStager
{
    private const string PluginGuid = "11c1ffe5-e4de-4cbb-a30c-c2870eb81603";
    private const string PluginVersion = "1.0.0.0";
    private const string TargetAbi = "10.11.0.0";

    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private static string? _cachedPublishDir;

    public static async Task<string> StageAsync(
        string pluginProjectPath,
        string pluginsRoot,
        CancellationToken ct)
    {
        var publishDir = await EnsurePublishedAsync(pluginProjectPath, ct);

        var pluginDir = Path.Combine(pluginsRoot, "NoriSync_" + PluginVersion);
        Directory.CreateDirectory(pluginDir);

        // Mirror the Jenkinsfile whitelist exactly — Microsoft.Data.Sqlite,
        // SQLitePCLRaw.*, TagLibSharp, etc. are provided by the Jellyfin host
        // process at runtime. Staging the full bin/ would mask a regression
        // where a genuinely-needed dep stops shipping.
        string[] shippedDlls =
        [
            "Jellyfin.Plugin.NoriSync.dll",
            "FftSharp.dll",
            "MessagePack.dll",
            "MessagePack.Annotations.dll",
            "Microsoft.NET.StringTools.dll",
        ];
        foreach (var dll in shippedDlls)
        {
            File.Copy(Path.Combine(publishDir, dll), Path.Combine(pluginDir, dll), overwrite: true);
        }

        var meta = new
        {
            category = "General",
            guid = PluginGuid,
            name = "NoriSync",
            description = "Integration test build",
            overview = "Integration test",
            owner = "test",
            targetAbi = TargetAbi,
            version = PluginVersion,
            changelog = string.Empty,
            timestamp = DateTime.UtcNow.ToString("O"),
            status = 0,
            autoUpdate = false,
        };
        await File.WriteAllTextAsync(
            Path.Combine(pluginDir, "meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        return pluginDir;
    }

    private static async Task<string> EnsurePublishedAsync(string pluginProjectPath, CancellationToken ct)
    {
        await PublishGate.WaitAsync(ct);
        try
        {
            if (_cachedPublishDir is not null && Directory.Exists(_cachedPublishDir))
            {
                return _cachedPublishDir;
            }

            var publishDir = Path.Combine(
                Path.GetTempPath(),
                "norisync-publish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(publishDir);

            await RunDotnetAsync(
                $"publish \"{pluginProjectPath}\" -c Release --no-self-contained " +
                $"-o \"{publishDir}\" --nologo " +
                $"-p:Version={PluginVersion} -p:AssemblyVersion={PluginVersion} -p:FileVersion={PluginVersion}",
                ct);

            _cachedPublishDir = publishDir;
            return publishDir;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    private static async Task RunDotnetAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {args} failed (exit {proc.ExitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
    }
}
