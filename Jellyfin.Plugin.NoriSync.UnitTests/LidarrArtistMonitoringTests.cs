using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// <see cref="LidarrArtistMonitoring"/> against a stand-in Lidarr: reading an artist's
/// monitoring, changing it through the editor (only the fields sent), and adding an artist
/// Lidarr hasn't got so it follows only future releases.
/// </summary>
public sealed class LidarrArtistMonitoringTests
{
    private const string Mbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";

    private static readonly LidarrAddDefaults Defaults = new("/music", 4, 2);

    [Fact]
    public async Task Get_ArtistNotInLidarr_IsNotInLidarr()
    {
        var lidarr = new FakeLidarr();
        var state = await NewService(lidarr).GetAsync(Mbid, CancellationToken.None);

        Assert.False(state.InLidarr);
        Assert.False(state.Monitored);
        Assert.Null(state.MonitorNewItems);
    }

    [Fact]
    public async Task Get_ArtistInLidarr_ReadsMonitoringAndRule()
    {
        var lidarr = new FakeLidarr { Artist = Artist(monitored: true, rule: "new") };
        var state = await NewService(lidarr).GetAsync(Mbid, CancellationToken.None);

        Assert.True(state.InLidarr);
        Assert.True(state.Monitored);
        Assert.Equal("new", state.MonitorNewItems);
        Assert.Contains(lidarr.Requests, r => r.Path == "artist" && r.Query == "?mbId=" + Mbid);
    }

    /// <summary>
    /// A Lidarr that ignores <c>mbId</c> lists every artist: another artist's row is never
    /// read as this one's.
    /// </summary>
    [Fact]
    public async Task Get_AnotherArtistsRow_IsIgnored()
    {
        var other = Artist(monitored: true, rule: "all").Replace(Mbid, "00000000-0000-0000-0000-000000000001", StringComparison.Ordinal);
        var lidarr = new FakeLidarr { ArtistListOverride = "[" + other + "]" };

        var state = await NewService(lidarr).GetAsync(Mbid, CancellationToken.None);

        Assert.False(state.InLidarr);
    }

    /// <summary>
    /// The editor gets only the fields asked for, so an unmonitor never resets the new-item
    /// rule (and no album flags are touched: no album-studio call).
    /// </summary>
    [Fact]
    public async Task Set_ExistingArtist_SendsOnlyTheChangedFields()
    {
        var lidarr = new FakeLidarr { Artist = Artist(monitored: true, rule: "all") };

        await NewService(lidarr).SetAsync(Mbid, new ArtistMonitoringRequest { Monitored = false }, Defaults, CancellationToken.None);

        var put = Assert.Single(lidarr.Requests, r => r.Method == "PUT");
        Assert.Equal("artist/editor", put.Path);
        using var doc = JsonDocument.Parse(put.Body);
        Assert.Equal(12, doc.RootElement.GetProperty("artistIds")[0].GetInt32());
        Assert.False(doc.RootElement.GetProperty("monitored").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("monitorNewItems", out _));
        Assert.DoesNotContain(lidarr.Requests, r => r.Path.StartsWith("albumstudio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Set_NewItemRuleOnly_LeavesMonitoredAlone()
    {
        var lidarr = new FakeLidarr { Artist = Artist(monitored: true, rule: "all") };

        await NewService(lidarr).SetAsync(Mbid, new ArtistMonitoringRequest { MonitorNewItems = "none" }, Defaults, CancellationToken.None);

        var put = Assert.Single(lidarr.Requests, r => r.Method == "PUT");
        using var doc = JsonDocument.Parse(put.Body);
        Assert.Equal("none", doc.RootElement.GetProperty("monitorNewItems").GetString());
        Assert.False(doc.RootElement.TryGetProperty("monitored", out _));
    }

    /// <summary>
    /// Monitoring an artist Lidarr hasn't got adds it following releases from now on: every
    /// album already out stays unmonitored (no back-catalogue download), no search, the
    /// configured folder and profiles, and tags present.
    /// </summary>
    [Fact]
    public async Task Set_MonitorArtistNotInLidarr_AddsItForFutureReleasesOnly()
    {
        var lidarr = new FakeLidarr();

        var state = await NewService(lidarr).SetAsync(Mbid, new ArtistMonitoringRequest { Monitored = true }, Defaults, CancellationToken.None);

        Assert.True(state.InLidarr);
        var post = Assert.Single(lidarr.Requests, r => r.Method == "POST");
        Assert.Equal("artist", post.Path);
        using var doc = JsonDocument.Parse(post.Body);
        var body = doc.RootElement;
        Assert.Equal(Mbid, body.GetProperty("foreignArtistId").GetString());
        Assert.Equal("Artist", body.GetProperty("artistName").GetString());
        Assert.True(body.GetProperty("monitored").GetBoolean());
        Assert.Equal("new", body.GetProperty("monitorNewItems").GetString());
        Assert.Equal("future", body.GetProperty("addOptions").GetProperty("monitor").GetString());
        Assert.False(body.GetProperty("addOptions").GetProperty("searchForMissingAlbums").GetBoolean());
        Assert.Equal("/music", body.GetProperty("rootFolderPath").GetString());
        Assert.Equal(4, body.GetProperty("qualityProfileId").GetInt32());
        Assert.Equal(2, body.GetProperty("metadataProfileId").GetInt32());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("tags").ValueKind);
    }

    [Fact]
    public async Task Set_UnmonitorArtistNotInLidarr_AddsNothing()
    {
        var lidarr = new FakeLidarr();

        var state = await NewService(lidarr).SetAsync(Mbid, new ArtistMonitoringRequest { Monitored = false }, Defaults, CancellationToken.None);

        Assert.False(state.InLidarr);
        Assert.DoesNotContain(lidarr.Requests, r => r.Method != "GET");
    }

    [Fact]
    public async Task Set_AddWithoutDefaults_IsRefused()
    {
        var lidarr = new FakeLidarr();

        var ex = await Assert.ThrowsAsync<LidarrUpstreamException>(() =>
            NewService(lidarr).SetAsync(Mbid, new ArtistMonitoringRequest { Monitored = true }, new LidarrAddDefaults(null, 0, 0), CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.DoesNotContain(lidarr.Requests, r => r.Method == "POST");
    }

    private static string Artist(bool monitored, string rule)
        => "{ \"id\": 12, \"artistName\": \"Artist\", \"foreignArtistId\": \"" + Mbid + "\", \"monitored\": "
            + (monitored ? "true" : "false") + ", \"monitorNewItems\": \"" + rule + "\" }";

    private static LidarrArtistMonitoring NewService(FakeLidarr lidarr)
        => new(new LidarrClient(NullLogger<LidarrClient>.Instance, lidarr, new Uri("http://lidarr:8686/"), "key"));

    private sealed record Sent(string Method, string Path, string Query, string Body);

    /// <summary>
    /// Lidarr's artist routes: one artist (or none), the editor and the add.
    /// </summary>
    private sealed class FakeLidarr : HttpMessageHandler
    {
        private readonly List<Sent> _requests = new();

        public string? Artist { get; set; }

        public string? ArtistListOverride { get; set; }

        public IReadOnlyList<Sent> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath["/api/v1/".Length..];
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (_requests)
            {
                _requests.Add(new Sent(request.Method.Method, path, uri.Query, body));
            }

            switch (request.Method.Method, path)
            {
                case ("GET", "artist"):
                    return Respond(HttpStatusCode.OK, ArtistListOverride ?? (Artist == null ? "[]" : "[" + Artist + "]"));
                case ("PUT", "artist/editor"):
                    return Respond(HttpStatusCode.Accepted, "[" + (Artist ?? "{}") + "]");
                case ("GET", "artist/lookup"):
                    return Respond(HttpStatusCode.OK, "[ { \"artistName\": \"Artist\", \"foreignArtistId\": \"" + Mbid + "\", \"monitored\": false } ]");
                case ("POST", "artist"):
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var created = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
                        var withId = new Dictionary<string, object?>(created.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))) { ["id"] = 12 };
                        return Respond(HttpStatusCode.Created, JsonSerializer.Serialize(withId));
                    }

                default:
                    return Respond(HttpStatusCode.NotFound, string.Empty);
            }
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
