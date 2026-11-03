using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.NoriSync.Configuration;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="loggerFactory">Logger factory for creating loggers.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        var dataPath = Path.Combine(applicationPaths.DataPath, "norisync");

        var storeLogger = loggerFactory.CreateLogger<ChangeTrackingStore>();
        Store = new ChangeTrackingStore(dataPath, storeLogger);

        var featureLogger = loggerFactory.CreateLogger<TrackFeatureStore>();
        FeatureStore = new TrackFeatureStore(dataPath, featureLogger);

        var instanceLogger = loggerFactory.CreateLogger<AppInstanceStore>();
        InstanceStore = new AppInstanceStore(dataPath, instanceLogger);

        var djLibraryLogger = loggerFactory.CreateLogger<DjLibraryStore>();
        DjLibraryStore = new DjLibraryStore(dataPath, djLibraryLogger);

        DeleteRetiredDatabase(dataPath, "selections.db", loggerFactory.CreateLogger<Plugin>());

        var requestsLogger = loggerFactory.CreateLogger<RequestsStore>();
        RequestsStore = new RequestsStore(dataPath, requestsLogger);

        var audioInfoLogger = loggerFactory.CreateLogger<AudioInfoStore>();
        AudioInfoStore = new AudioInfoStore(dataPath, audioInfoLogger);

        var artworkLogger = loggerFactory.CreateLogger<AnimatedArtworkStore>();
        AnimatedArtworkStore = new AnimatedArtworkStore(dataPath, artworkLogger);

        var coverDesignLogger = loggerFactory.CreateLogger<PlaylistCoverDesignStore>();
        CoverDesignStore = new PlaylistCoverDesignStore(dataPath, coverDesignLogger);
    }

    /// <inheritdoc />
    public override string Name => "NoriSync";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("11c1ffe5-e4de-4cbb-a30c-c2870eb81603");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the change tracking store.
    /// </summary>
    public ChangeTrackingStore Store { get; }

    /// <summary>
    /// Gets the per-track waveform feature store.
    /// </summary>
    public TrackFeatureStore FeatureStore { get; }

    /// <summary>
    /// Gets the registered client app instance store.
    /// </summary>
    public AppInstanceStore InstanceStore { get; }

    /// <summary>
    /// Gets the store of per-user DJ library ids (the secret key for each user's library on the
    /// external DJ service).
    /// </summary>
    public DjLibraryStore DjLibraryStore { get; }

    /// <summary>
    /// Gets the Lidarr requests store.
    /// </summary>
    public RequestsStore RequestsStore { get; }

    /// <summary>
    /// Gets the cache of computed exact audio-decode metadata.
    /// </summary>
    public AudioInfoStore AudioInfoStore { get; }

    /// <summary>
    /// Gets the store of animated-artwork assets (short looping videos) for albums and artists.
    /// </summary>
    public AnimatedArtworkStore AnimatedArtworkStore { get; }

    /// <summary>
    /// Gets the store of playlist cover designs (the editable source of covers made in Nori's
    /// cover designer).
    /// </summary>
    public PlaylistCoverDesignStore CoverDesignStore { get; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "NoriSyncArtwork",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.artworkManager.html", GetType().Namespace)
            }
        ];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the plugin and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            Store.Dispose();
            FeatureStore.Dispose();
            InstanceStore.Dispose();
            DjLibraryStore.Dispose();
            RequestsStore.Dispose();
            AudioInfoStore.Dispose();
            AnimatedArtworkStore.Dispose();
            CoverDesignStore.Dispose();
        }

        _disposed = true;
    }

    // Removes the database of a retired feature, with its WAL side files. selections.db held the
    // account-wide library selection, which Nori dropped (library selection is per device again)
    // before any released build called it. Best-effort — a failure only leaves the file behind.
    private static void DeleteRetiredDatabase(string dataPath, string fileName, ILogger logger)
    {
        var dbPath = Path.Combine(dataPath, fileName);
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    logger.LogInformation("Deleted retired database file {Path}", path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete retired database file {Path}", path);
            }
        }
    }
}
