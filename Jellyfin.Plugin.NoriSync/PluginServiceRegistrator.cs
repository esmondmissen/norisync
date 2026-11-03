using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

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
    }
}
