using System;
using System.Collections.Generic;
using Jellyfin.Plugin.NoriSync.Configuration;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// The two album backends, and which one takes a new album request: the only one set up, or the
/// admin's choice (<see cref="PluginConfiguration.AlbumRequestBackend"/>) when both are. A
/// request already made stays with the backend it went to.
/// </summary>
public sealed class AlbumBackends : IDisposable
{
    private readonly Func<PluginConfiguration?> _config;
    private readonly IDisposable[] _owned;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlbumBackends"/> class: Lidarr on the
    /// injected services, SoulSync on its own client for its Lidarr-compatible API.
    /// </summary>
    /// <param name="lidarr">The Lidarr client.</param>
    /// <param name="lidarrScheduler">Lidarr's search schedule.</param>
    /// <param name="lidarrMonitoring">Lidarr's artist monitoring.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public AlbumBackends(
        LidarrClient lidarr,
        AlbumSearchScheduler lidarrScheduler,
        LidarrArtistMonitoring lidarrMonitoring,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var compat = LidarrClient.ForSoulSyncCompat(loggerFactory.CreateLogger<LidarrClient>());
        var compatScheduler = new AlbumSearchScheduler(compat, loggerFactory.CreateLogger<AlbumSearchScheduler>());
        _owned = new IDisposable[] { compat, compatScheduler };
        _config = () => Plugin.Instance?.Configuration;

        Lidarr = new AlbumBackend(lidarr, lidarrScheduler, lidarrMonitoring, () => Plugin.Instance?.RequestsStore, ConfiguredLidarrDefaults);
        SoulSync = new AlbumBackend(compat, compatScheduler, new LidarrArtistMonitoring(compat), () => Plugin.Instance?.SoulSyncRequestsStore, ConfiguredLidarrDefaults);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AlbumBackends"/> class from given backends,
    /// for tests.
    /// </summary>
    /// <param name="lidarr">The Lidarr backend.</param>
    /// <param name="soulSync">The SoulSync backend.</param>
    /// <param name="config">The configuration to read the admin's choice from.</param>
    internal AlbumBackends(AlbumBackend lidarr, AlbumBackend soulSync, Func<PluginConfiguration?> config)
    {
        Lidarr = lidarr;
        SoulSync = soulSync;
        _config = config;
        _owned = Array.Empty<IDisposable>();
    }

    /// <summary>
    /// Gets the Lidarr backend.
    /// </summary>
    public AlbumBackend Lidarr { get; }

    /// <summary>
    /// Gets the SoulSync backend (its Lidarr-compatible API).
    /// </summary>
    public AlbumBackend SoulSync { get; }

    /// <summary>
    /// Gets both backends, Lidarr first.
    /// </summary>
    public IReadOnlyList<AlbumBackend> All => new[] { Lidarr, SoulSync };

    /// <summary>
    /// Gets the backend a new album request goes to, or null when neither is set up.
    /// </summary>
    public AlbumBackend? Active => Choose(Lidarr.IsConfigured, SoulSync.IsConfigured, _config()?.AlbumRequestBackend ?? RequestBackend.Lidarr) switch
    {
        RequestBackend.Lidarr => Lidarr,
        RequestBackend.SoulSync => SoulSync,
        _ => null,
    };

    /// <summary>
    /// Which backend takes new album requests.
    /// </summary>
    /// <param name="lidarrConfigured">Whether Lidarr is set up.</param>
    /// <param name="soulSyncConfigured">Whether SoulSync is set up.</param>
    /// <param name="chosen">The admin's choice, read only when both are.</param>
    /// <returns>The backend, or null when neither is set up.</returns>
    internal static RequestBackend? Choose(bool lidarrConfigured, bool soulSyncConfigured, RequestBackend chosen)
        => (lidarrConfigured, soulSyncConfigured) switch
        {
            (true, true) => chosen,
            (true, false) => RequestBackend.Lidarr,
            (false, true) => RequestBackend.SoulSync,
            _ => null,
        };

    /// <summary>
    /// The backend of that kind.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The backend.</returns>
    public AlbumBackend For(RequestBackend kind) => kind == RequestBackend.SoulSync ? SoulSync : Lidarr;

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    private static LidarrAddDefaults ConfiguredLidarrDefaults()
    {
        var config = Plugin.Instance?.Configuration;
        return new LidarrAddDefaults(config?.LidarrRootFolderPath, config?.LidarrQualityProfileId ?? 0, config?.LidarrMetadataProfileId ?? 0);
    }
}
