using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Reads and changes whether Lidarr follows an artist, by MusicBrainz artist id. Changes go
/// through <c>PUT artist/editor</c>, which sets only the fields sent: <c>PUT artist/{id}</c>
/// needs the whole resource and always queues a folder move. Neither touches the artist's
/// albums' own monitored flags.
/// </summary>
public sealed class LidarrArtistMonitoring
{
    private readonly LidarrClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="LidarrArtistMonitoring"/> class.
    /// </summary>
    /// <param name="client">The Lidarr client.</param>
    public LidarrArtistMonitoring(LidarrClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is one of Lidarr's new-item rules.
    /// </summary>
    /// <param name="value">The rule as sent.</param>
    /// <returns>True for <c>all</c>, <c>none</c> or <c>new</c>.</returns>
    public static bool IsNewItemRule(string? value) => value is "all" or "none" or "new";

    /// <summary>
    /// The artist's monitoring as Lidarr has it.
    /// </summary>
    /// <param name="mbid">MusicBrainz artist id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state; <see cref="ArtistMonitoring.InLidarr"/> false when Lidarr hasn't the artist.</returns>
    /// <exception cref="LidarrUpstreamException">Lidarr answered with an error.</exception>
    public async Task<ArtistMonitoring> GetAsync(string mbid, CancellationToken cancellationToken)
    {
        var artist = await FindArtistAsync(mbid, cancellationToken).ConfigureAwait(false);
        return artist is { } found ? Read(found) : new ArtistMonitoring();
    }

    /// <summary>
    /// Changes the artist's monitoring. An artist not in Lidarr is added only when asked to be
    /// monitored, and then follows only what is released from now on (its existing albums stay
    /// unmonitored) — following an artist must not download its whole back catalogue.
    /// </summary>
    /// <param name="mbid">MusicBrainz artist id.</param>
    /// <param name="change">The fields to change.</param>
    /// <param name="defaults">Root folder and profiles for an artist Lidarr hasn't got.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state afterwards.</returns>
    /// <exception cref="LidarrUpstreamException">Lidarr answered with an error.</exception>
    public async Task<ArtistMonitoring> SetAsync(
        string mbid,
        ArtistMonitoringRequest change,
        LidarrAddDefaults defaults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(defaults);

        var existing = await FindArtistAsync(mbid, cancellationToken).ConfigureAwait(false);
        if (existing is not { } artist)
        {
            return change.Monitored == true
                ? await AddAsync(mbid, change.MonitorNewItems ?? "new", defaults, cancellationToken).ConfigureAwait(false)
                : new ArtistMonitoring();
        }

        if (change.Monitored == null && change.MonitorNewItems == null)
        {
            return Read(artist);
        }

        var id = artist.GetProperty("id").GetInt32();
        var body = new JsonObject { ["artistIds"] = new JsonArray(id) };
        if (change.Monitored is bool monitored)
        {
            body["monitored"] = monitored;
        }

        if (change.MonitorNewItems != null)
        {
            body["monitorNewItems"] = change.MonitorNewItems;
        }

        using (var response = await SendJsonAsync(HttpMethod.Put, "artist/editor", body, cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return await GetAsync(mbid, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtistMonitoring> AddAsync(string mbid, string monitorNewItems, LidarrAddDefaults defaults, CancellationToken cancellationToken)
    {
        if (!defaults.IsComplete)
        {
            throw new LidarrUpstreamException(HttpStatusCode.BadRequest, "Lidarr defaults (root folder, quality profile, metadata profile) are not configured.");
        }

        JsonObject artist;
        using (var lookup = await _client.SendAsync(
            HttpMethod.Get,
            "artist/lookup",
            "?term=lidarr:" + Uri.EscapeDataString(mbid),
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(lookup, cancellationToken).ConfigureAwait(false);
            var bytes = await lookup.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(bytes) is not JsonArray results || results.Count == 0 || results[0] is not JsonObject first)
            {
                throw new LidarrUpstreamException(HttpStatusCode.NotFound, "Artist not found in MusicBrainz.");
            }

            artist = first.DeepClone().AsObject();
        }

        artist["qualityProfileId"] = defaults.QualityProfileId;
        artist["metadataProfileId"] = defaults.MetadataProfileId;
        artist["rootFolderPath"] = defaults.RootFolderPath;
        artist["monitored"] = true;
        artist["monitorNewItems"] = monitorNewItems;

        // Left out, Lidarr stores null tags, and its indexer tag filter reads them.
        artist["tags"] ??= new JsonArray();

        // "future" leaves every album already out unmonitored; "none" would unmonitor the artist.
        artist["addOptions"] = new JsonObject
        {
            ["monitor"] = "future",
            ["searchForMissingAlbums"] = false
        };

        using var added = await SendJsonAsync(HttpMethod.Post, "artist", artist, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(added, cancellationToken).ConfigureAwait(false);
        var addedBytes = await added.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(addedBytes);
        return Read(doc.RootElement);
    }

    private async Task<JsonElement?> FindArtistAsync(string mbid, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(
            HttpMethod.Get,
            "artist",
            "?mbId=" + Uri.EscapeDataString(mbid),
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);
        foreach (var artist in doc.RootElement.EnumerateArray())
        {
            // Lidarr filters by mbId, but an older one ignored the parameter and listed every
            // artist: never take another artist's settings for this one's.
            if (artist.TryGetProperty("foreignArtistId", out var foreign)
                && string.Equals(foreign.GetString(), mbid, StringComparison.OrdinalIgnoreCase)
                && artist.TryGetProperty("id", out var id) && id.TryGetInt32(out _))
            {
                return artist.Clone();
            }
        }

        return null;
    }

    private static ArtistMonitoring Read(JsonElement artist) => new()
    {
        InLidarr = true,
        Monitored = artist.TryGetProperty("monitored", out var monitored) && monitored.ValueKind == JsonValueKind.True,
        MonitorNewItems = artist.TryGetProperty("monitorNewItems", out var rule) && rule.ValueKind == JsonValueKind.String
            ? rule.GetString()
            : null,
    };

    private Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, JsonNode body, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
        content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);
        return _client.SendAsync(method, path, queryString: null, content, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new LidarrUpstreamException(response.StatusCode, body);
    }
}
