using System;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync;

/// <summary>
/// Registers plugin services with the Jellyfin dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<LibraryChangeTracker>();
        serviceCollection.AddSingleton<FfmpegAudioDecoder>();
        serviceCollection.AddSingleton<AudioInfoProbe>();
        serviceCollection.AddSingleton<ArtworkProbe>();
        serviceCollection.AddSingleton<ArtworkTranscoder>();
        serviceCollection.AddSingleton<LidarrClient>();
        serviceCollection.AddSingleton<RemoteImageFetcher>();
        serviceCollection.AddSingleton<MusicBrainzClient>();
        serviceCollection.AddSingleton<AppleMusicArtworkClient>();
        serviceCollection.AddSingleton<HlsVariantResolver>();
        serviceCollection.AddSingleton<ArtworkDerivativeCache>();
        serviceCollection.AddMemoryCache();

        // One instance, both as Jellyfin's socket listener and for playback sync's pushes and pauses.
        serviceCollection.AddSingleton<DeviceSocketRegistry>();
        serviceCollection.AddSingleton<IWebSocketListener>(services => services.GetRequiredService<DeviceSocketRegistry>());
        serviceCollection.AddSingleton(services => new PlaybackHub(
            () => Plugin.Instance?.PlaybackStore,
            services.GetRequiredService<DeviceSocketRegistry>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<PlaybackHub>(),
            TimeProvider.System));
    }
}
