using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Api;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Interactive search's results as the plugin passes them on (<see cref="ReleaseCandidate"/>),
/// the grab body, and the longer limit an interactive search gets.
/// </summary>
public sealed class ReleaseCandidateTests
{
    private const string LidarrReleases = """
    [
      {
        "guid": "g-1", "indexerId": 3, "indexer": "Tracker", "title": "Artist - Album (2020) [FLAC]",
        "size": 412000000, "quality": { "quality": { "id": 6, "name": "FLAC" }, "revision": { "version": 1 } },
        "protocol": "torrent", "seeders": 12, "leechers": 1, "ageHours": 50.5, "releaseGroup": "GRP",
        "approved": true, "temporarilyRejected": false, "rejected": false, "rejections": [],
        "downloadUrl": "https://tracker/download?apikey=SECRET", "magnetUrl": "magnet:?xt=SECRET",
        "infoUrl": "https://tracker/info?apikey=SECRET", "commentUrl": "https://tracker/c?apikey=SECRET"
      },
      {
        "guid": "g-2", "indexerId": 4, "title": "Artist - Album [MP3]", "size": 0, "protocol": "usenet",
        "approved": false, "rejected": true, "rejections": ["Not an upgrade", "", "Wrong track count"]
      },
      { "indexerId": 5, "title": "no guid" },
      { "guid": "g-4", "title": "no indexer" }
    ]
    """;

    /// <summary>
    /// The URLs indexers put their API keys in never reach a client.
    /// </summary>
    [Fact]
    public void FromLidarr_DropsEveryDownloadUrl()
    {
        using var doc = JsonDocument.Parse(LidarrReleases);
        var json = JsonSerializer.Serialize(ReleaseCandidate.FromLidarr(doc.RootElement));

        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Url", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fields are read into the picker's shape, in Lidarr's order, and entries that couldn't be
    /// grabbed (no guid or indexer) are left out.
    /// </summary>
    [Fact]
    public void FromLidarr_ReadsTheCandidatesInLidarrsOrder()
    {
        using var doc = JsonDocument.Parse(LidarrReleases);
        var candidates = ReleaseCandidate.FromLidarr(doc.RootElement);

        Assert.Equal(new[] { "g-1", "g-2" }, candidates.Select(c => c.Guid));

        var flac = candidates[0];
        Assert.Equal(3, flac.IndexerId);
        Assert.Equal("Tracker", flac.Indexer);
        Assert.Equal(412000000, flac.Size);
        Assert.Equal("FLAC", flac.Quality);
        Assert.Equal("torrent", flac.Protocol);
        Assert.Equal(12, flac.Seeders);
        Assert.Equal(1, flac.Leechers);
        Assert.Equal(50.5, flac.AgeHours);
        Assert.Equal("GRP", flac.ReleaseGroup);
        Assert.True(flac.Approved);
        Assert.Empty(flac.Rejections);

        var mp3 = candidates[1];
        Assert.Null(mp3.Size);
        Assert.Null(mp3.Quality);
        Assert.Null(mp3.Seeders);
        Assert.True(mp3.Rejected);
        Assert.Equal(new[] { "Not an upgrade", "Wrong track count" }, mp3.Rejections);
    }

    [Fact]
    public void FromLidarr_NotAnArray_IsEmpty()
    {
        using var doc = JsonDocument.Parse("""{ "message": "nope" }""");
        Assert.Empty(ReleaseCandidate.FromLidarr(doc.RootElement));
    }

    /// <summary>
    /// The grab names the album (and artist), so Lidarr can take a result it couldn't match
    /// to the album by title.
    /// </summary>
    [Fact]
    public void GrabBody_NamesTheAlbumAndArtist()
    {
        var body = LidarrController.BuildGrabBody("g-1", 3, albumId: 7, artistId: 12);
        Assert.Equal("g-1", (string?)body["guid"]);
        Assert.Equal(3, (int?)body["indexerId"]);
        Assert.Equal(7, (int?)body["albumId"]);
        Assert.Equal(12, (int?)body["artistId"]);

        Assert.False(LidarrController.BuildGrabBody("g-1", 3, 7, null).ContainsKey("artistId"));
    }

    /// <summary>
    /// A call is bounded by its own limit (an interactive search gets minutes, not the
    /// default minute), and a timeout surfaces as the cancellation callers treat as one.
    /// </summary>
    [Fact]
    public async Task Send_PerCallTimeout_CancelsASlowLidarr()
    {
        using var client = new LidarrClient(NullLogger<LidarrClient>.Instance, new SlowLidarr(), new Uri("http://lidarr:8686/"), "key");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(
            HttpMethod.Get, "release", "?albumId=1", null, HttpCompletionOption.ResponseContentRead,
            CancellationToken.None, TimeSpan.FromMilliseconds(50)));
        Assert.True(LidarrClient.InteractiveSearchTimeout > LidarrClient.DefaultTimeout);
    }

    private sealed class SlowLidarr : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
