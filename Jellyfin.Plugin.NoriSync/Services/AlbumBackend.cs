using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// One service album requests can go to: Lidarr, or SoulSync through its Lidarr-compatible API.
/// Both answer the same Lidarr routes, so the album code is the same for both; what differs is
/// here.
/// </summary>
public sealed class AlbumBackend
{
    /// <summary>
    /// How long SoulSync's root folder and profiles are kept before being read again.
    /// </summary>
    private static readonly TimeSpan DefaultsLifetime = TimeSpan.FromMinutes(10);

    private readonly Func<RequestsStore?> _store;
    private readonly Func<LidarrAddDefaults> _configuredDefaults;
    private readonly object _defaultsLock = new();
    private (LidarrAddDefaults Defaults, DateTimeOffset ReadAt)? _readDefaults;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlbumBackend"/> class.
    /// </summary>
    /// <param name="client">The client for this backend's Lidarr API.</param>
    /// <param name="scheduler">The search schedule, on the same client.</param>
    /// <param name="artistMonitoring">Artist monitoring, on the same client.</param>
    /// <param name="store">This backend's requests store (null before the plugin is up).</param>
    /// <param name="configuredDefaults">Lidarr's root folder and profiles, from the configuration. Unused for SoulSync, whose are read from it.</param>
    public AlbumBackend(
        LidarrClient client,
        AlbumSearchScheduler scheduler,
        LidarrArtistMonitoring artistMonitoring,
        Func<RequestsStore?> store,
        Func<LidarrAddDefaults> configuredDefaults)
    {
        ArgumentNullException.ThrowIfNull(client);
        Client = client;
        Scheduler = scheduler;
        ArtistMonitoring = artistMonitoring;
        _store = store;
        _configuredDefaults = configuredDefaults;
    }

    /// <summary>
    /// Gets which backend this is.
    /// </summary>
    public RequestBackend Kind => Client.Backend;

    /// <summary>
    /// Gets the name to show for it.
    /// </summary>
    public string Name => Kind == RequestBackend.SoulSync ? "SoulSync" : "Lidarr";

    /// <summary>
    /// Gets the client for its Lidarr API.
    /// </summary>
    public LidarrClient Client { get; }

    /// <summary>
    /// Gets its album search schedule.
    /// </summary>
    public AlbumSearchScheduler Scheduler { get; }

    /// <summary>
    /// Gets its artist monitoring.
    /// </summary>
    public LidarrArtistMonitoring ArtistMonitoring { get; }

    /// <summary>
    /// Gets its requests store, or null before the plugin is up.
    /// </summary>
    public RequestsStore? Store => _store();

    /// <summary>
    /// Gets a value indicating whether it is set up.
    /// </summary>
    public bool IsConfigured => Client.TryGetBaseUri() != null;

    /// <summary>
    /// Gets a value indicating whether it lists release candidates for an interactive search.
    /// SoulSync's Lidarr-compatible API has no <c>release</c> route.
    /// </summary>
    public bool SupportsInteractiveSearch => Kind == RequestBackend.Lidarr;

    /// <summary>
    /// Gets a value indicating whether its <c>track?albumId=</c> lists an album's tracks. SoulSync's
    /// answers an empty list (its tracks live in its own library), so the tracklist comes from
    /// MusicBrainz instead.
    /// </summary>
    public bool ListsAlbumTracks => Kind == RequestBackend.Lidarr;

    /// <summary>
    /// The root folder and profiles new artists and albums are added with. Lidarr's are the
    /// admin's choice in the configuration; SoulSync's Lidarr-compatible API has its own (one
    /// root folder, its quality and metadata profiles), so the first of each is used and no
    /// setup is asked for.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The defaults; <see cref="LidarrAddDefaults.IsComplete"/> false when they can't be had.</returns>
    public async Task<LidarrAddDefaults> GetAddDefaultsAsync(CancellationToken cancellationToken)
    {
        if (Kind == RequestBackend.Lidarr)
        {
            return _configuredDefaults();
        }

        lock (_defaultsLock)
        {
            if (_readDefaults is { } cached && DateTimeOffset.UtcNow - cached.ReadAt < DefaultsLifetime)
            {
                return cached.Defaults;
            }
        }

        var rootTask = FirstAsync("rootfolder", "path", cancellationToken);
        var qualityTask = FirstAsync("qualityprofile", "id", cancellationToken);
        var metadataTask = FirstAsync("metadataprofile", "id", cancellationToken);
        await Task.WhenAll(rootTask, qualityTask, metadataTask).ConfigureAwait(false);

        var defaults = new LidarrAddDefaults(
            await rootTask.ConfigureAwait(false),
            int.TryParse(await qualityTask.ConfigureAwait(false), out var quality) ? quality : 0,
            int.TryParse(await metadataTask.ConfigureAwait(false), out var metadata) ? metadata : 0);
        if (defaults.IsComplete)
        {
            lock (_defaultsLock)
            {
                _readDefaults = (defaults, DateTimeOffset.UtcNow);
            }
        }

        return defaults;
    }

    private async Task<string?> FirstAsync(string path, string property, CancellationToken cancellationToken)
    {
        using var response = await Client.SendAsync(
            HttpMethod.Get,
            path,
            queryString: null,
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value))
                {
                    var text = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.Number => value.GetRawText(),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
