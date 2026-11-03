using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.IntegrationTests;

public sealed class JellyfinFixture : IAsyncLifetime
{
    private const string AdminUser = "admin";
    private const string AdminPass = "admin-pw-integration";

    private IContainer? _container;
    private string? _stageRoot;

    public JellyfinApiClient Client { get; private set; } = null!;
    public Uri BaseAddress { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        var pluginProjectPath = Path.Combine(repoRoot, "Jellyfin.Plugin.NoriSync", "Jellyfin.Plugin.NoriSync.csproj");

        _stageRoot = Path.Combine(Path.GetTempPath(), "norisync-it-" + Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(_stageRoot, "config");
        var cacheDir = Path.Combine(_stageRoot, "cache");
        var mediaDir = Path.Combine(_stageRoot, "media", "music");
        var pluginsDir = Path.Combine(configDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(mediaDir);

        // Seed a single short sine-wave track so the NoriSync task has a real
        // file to decode and run feature extraction over.
        var audioFilePath = Path.Combine(mediaDir, "test-track.wav");
        MediaSeeder.WriteSineWav(audioFilePath,
            sampleRate: 16000, seconds: 2, frequencyHz: 440);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await PluginStager.StageAsync(pluginProjectPath, pluginsDir, cts.Token);

        // Pre-seed the plugin's XML config. Writing the XML directly (rather than
        // POST /Plugins/{id}/Configuration) sidesteps any case-sensitivity quirks in
        // Jellyfin's JSON binding of the plugin-specific config type.
        var configsDir = Path.Combine(configDir, "plugins", "configurations");
        Directory.CreateDirectory(configsDir);
        // BasePluginOfT.ConfigurationFileName derives from the assembly filename,
        // not the plugin display name — so this must be Jellyfin.Plugin.NoriSync.xml.
        await File.WriteAllTextAsync(
            Path.Combine(configsDir, "Jellyfin.Plugin.NoriSync.xml"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <RetentionDays>30</RetentionDays>
              <MaxTracksPerRun>0</MaxTracksPerRun>
              <AnalysisConcurrency>2</AnalysisConcurrency>
            </PluginConfiguration>
            """,
            cts.Token);

        // Jellyfin's official image runs as uid 1000. Make the staged config
        // world-readable so tests work regardless of the host's uid.
        MakeWorldReadable(_stageRoot);

        // The same plugin build is expected to work on every supported server
        // line; JELLYFIN_IMAGE is what makes this suite a cross-version matrix.
        var image = Environment.GetEnvironmentVariable("JELLYFIN_IMAGE")
            ?? "jellyfin/jellyfin:10.11.11";

        _container = new ContainerBuilder(image)
            .WithPortBinding(8096, assignRandomHostPort: true)
            .WithBindMount(configDir, "/config")
            .WithBindMount(cacheDir, "/cache")
            .WithBindMount(Path.GetDirectoryName(mediaDir)!, "/media")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Startup complete"))
            .Build();

        await _container.StartAsync(cts.Token);

        var hostPort = _container.GetMappedPublicPort(8096);
        BaseAddress = new Uri($"http://{_container.Hostname}:{hostPort}");
        Client = new JellyfinApiClient(BaseAddress);

        try
        {
            await Client.WaitForHttpReadyAsync(TimeSpan.FromMinutes(1), cts.Token);
            await Client.CompleteStartupWizardAsync(AdminUser, AdminPass, cts.Token);

            await Client.AddMusicLibraryAsync("/media/music", cts.Token);
            await Client.WaitForAudioItemsAsync(minimum: 1, TimeSpan.FromMinutes(2), cts.Token);
        }
        catch
        {
            var logs = await GetContainerLogsAsync();
            Console.WriteLine("===== JELLYFIN CONTAINER LOGS (on init failure) =====");
            Console.WriteLine(logs);
            Console.WriteLine("===== END LOGS =====");
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        if (_stageRoot is not null && Directory.Exists(_stageRoot))
        {
            try { Directory.Delete(_stageRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    public async Task<string> GetContainerLogsAsync()
    {
        if (_container is null)
        {
            return string.Empty;
        }
        var (stdout, stderr) = await _container.GetLogsAsync();
        return stdout + "\n" + stderr;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Jellyfin.Plugin.NoriSync.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException(
            "Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static void MakeWorldReadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("chmod", $"-R a+rX \"{path}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(5000);
        }
        catch
        {
            // best-effort; on macOS Docker Desktop perms don't matter anyway
        }
    }
}

[CollectionDefinition("Jellyfin")]
public sealed class JellyfinCollection : ICollectionFixture<JellyfinFixture>
{
}
