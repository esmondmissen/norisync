using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Album requests on SoulSync's Lidarr-compatible API: the backend choice, the store kept apart
/// from Lidarr's, and the search schedule's SoulSync dialect (no metadata to wait for, "N tracks
/// queued", no re-search once queued). Shapes from SoulSync's <c>core/lidarr_compat</c>.
/// </summary>
public sealed class SoulSyncAlbumTests : IDisposable
{
    // As compat's album_resource answers: no releases, duration 0.
    private const string CompatAlbum = """{ "id": 3, "title": "Discovery", "artist": { "artistName": "Daft Punk" }, "releases": [], "duration": 0, "statistics": { "trackFileCount": 0, "totalTrackCount": 14 } }""";

    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "norisync-soulsync-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    /// <summary>
    /// The only backend set up takes albums whatever the admin's choice says; with both, the
    /// choice decides; with neither, there is none.
    /// </summary>
    [Theory]
    [InlineData(true, true, RequestBackend.Lidarr, RequestBackend.Lidarr)]
    [InlineData(true, true, RequestBackend.SoulSync, RequestBackend.SoulSync)]
    [InlineData(true, false, RequestBackend.SoulSync, RequestBackend.Lidarr)]
    [InlineData(false, true, RequestBackend.Lidarr, RequestBackend.SoulSync)]
    public void Choose_PicksTheOnlyOneOrTheAdminsChoice(bool lidarr, bool soulSync, RequestBackend chosen, RequestBackend expected)
        => Assert.Equal(expected, AlbumBackends.Choose(lidarr, soulSync, chosen));

    /// <summary>
    /// With neither set up, album requests are off.
    /// </summary>
    [Fact]
    public void Choose_NeitherSetUp_IsNone() => Assert.Null(AlbumBackends.Choose(false, false, RequestBackend.SoulSync));

    /// <summary>
    /// The compat API sits under <c>/lidarr</c> of the SoulSync URL, a URL base kept.
    /// </summary>
    [Theory]
    [InlineData("http://soulsync:8008", "http://soulsync:8008/lidarr/")]
    [InlineData("http://soulsync:8008/", "http://soulsync:8008/lidarr/")]
    [InlineData("https://host/soulsync", "https://host/soulsync/lidarr/")]
    public void CompatBaseUri_IsUnderLidarr(string url, string expected)
        => Assert.Equal(expected, LidarrClient.SoulSyncCompatBaseUri(url)!.ToString());

    /// <summary>
    /// SoulSync's album ids are its own: the same id in each backend's store is two requests,
    /// and every row read says which backend it came from.
    /// </summary>
    [Fact]
    public void Stores_KeepTheBackendsApart()
    {
        using var lidarr = new RequestsStore(_dataPath, NullLogger.Instance);
        using var soulSync = new RequestsStore(_dataPath, NullLogger.Instance, RequestBackend.SoulSync);
        var user = Guid.NewGuid();
        lidarr.Upsert(Row(user, 3, "Lidarr's album 3"));
        soulSync.Upsert(Row(user, 3, "SoulSync's album 3"));

        Assert.Equal("Lidarr's album 3", Assert.Single(lidarr.ListByUser(user)).AlbumTitle);
        var fromSoulSync = Assert.Single(soulSync.ListByUser(user));
        Assert.Equal("SoulSync's album 3", fromSoulSync.AlbumTitle);
        Assert.Equal(RequestBackend.SoulSync, fromSoulSync.Backend);
        Assert.Equal(RequestBackend.Lidarr, lidarr.ListByUser(user)[0].Backend);
        Assert.True(File.Exists(Path.Combine(_dataPath, "soulsync-requests.db")));
    }

    /// <summary>
    /// SoulSync reads the tracklist from MusicBrainz when it searches, so an album without
    /// releases is searched at once instead of waiting an hour for metadata that never comes.
    /// </summary>
    [Fact]
    public async Task Advance_OnSoulSync_SearchesWithoutWaitingForReleases()
    {
        var fake = new FakeSoulSync();
        fake.On("POST lidarr/command", HttpStatusCode.Created, """{ "id": 5, "status": "queued" }""");
        using var scheduler = new AlbumSearchScheduler(FakeSoulSync.CompatClient(fake), NullLogger<AlbumSearchScheduler>.Instance);

        var (record, searched) = await scheduler.AdvanceAsync(AlbumSearchScheduler.Fresh(3, T0), Json(CompatAlbum), T0.AddMinutes(1), true, CancellationToken.None);

        Assert.True(searched);
        Assert.Equal(AlbumSearchState.Searching, record.State);
        Assert.Equal(5, record.CommandId);
        Assert.Contains("AlbumSearch", Assert.Single(fake.Requests("POST", "lidarr/command")).Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// "12 tracks queued" is a find: the tracks are on the wishlist, which retries them itself, so
    /// nothing is searched again (another search would only re-add them).
    /// </summary>
    [Fact]
    public async Task Advance_OnSoulSync_TracksQueuedIsGrabbedAndNotSearchedAgain()
    {
        var fake = new FakeSoulSync();
        fake.On("GET lidarr/command/5", HttpStatusCode.OK, """{ "id": 5, "status": "completed", "message": "12 tracks queued" }""");
        using var scheduler = new AlbumSearchScheduler(FakeSoulSync.CompatClient(fake), NullLogger<AlbumSearchScheduler>.Instance);
        var searching = new AlbumSearchRecord { LidarrAlbumId = 3, State = AlbumSearchState.Searching, Attempts = 1, WindowStart = T0, CommandId = 5 };

        var (record, searched) = await scheduler.AdvanceAsync(searching, Json(CompatAlbum), T0.AddDays(2), true, CancellationToken.None);

        Assert.False(searched);
        Assert.Equal(AlbumSearchState.Grabbed, record.State);
        Assert.Null(record.NextSearchAt);
        Assert.Equal("12 tracks queued", record.LastMessage);
    }

    /// <summary>
    /// "0 tracks queued" (MusicBrainz had no tracklist yet) is a miss, searched again on the
    /// usual backoff.
    /// </summary>
    [Fact]
    public async Task Advance_OnSoulSync_NoTracksQueuedIsAMissWithBackoff()
    {
        var fake = new FakeSoulSync();
        fake.On("GET lidarr/command/5", HttpStatusCode.OK, """{ "id": 5, "status": "completed", "message": "0 tracks queued" }""");
        using var scheduler = new AlbumSearchScheduler(FakeSoulSync.CompatClient(fake), NullLogger<AlbumSearchScheduler>.Instance);
        var searching = new AlbumSearchRecord { LidarrAlbumId = 3, State = AlbumSearchState.Searching, Attempts = 1, WindowStart = T0, CommandId = 5 };

        var (record, _) = await scheduler.AdvanceAsync(searching, Json(CompatAlbum), T0.AddMinutes(1), false, CancellationToken.None);

        Assert.Equal(AlbumSearchState.NothingFound, record.State);
        Assert.Equal(T0.AddMinutes(16), record.NextSearchAt);
    }

    /// <summary>
    /// The compat API's "N tracks queued" is read, and Lidarr's line isn't mistaken for it.
    /// </summary>
    [Theory]
    [InlineData("12 tracks queued", 12)]
    [InlineData("1 track queued", 1)]
    [InlineData("0 tracks queued", 0)]
    [InlineData("Completed. 3 reports downloaded.", null)]
    [InlineData(null, null)]
    public void TracksQueued_ReadsTheCompatMessage(string? message, int? expected)
        => Assert.Equal(expected, AlbumSearchScheduler.TracksQueued(message));

    /// <summary>
    /// The compat queue is always empty, so a SoulSync album's progress comes from SoulSync's
    /// download tasks for its tracks: the mean of those downloading, else importing.
    /// </summary>
    [Fact]
    public void AlbumActivity_ComesFromTheAlbumsDownloadTasks()
    {
        var album = Json(CompatAlbum);
        var downloading = new[]
        {
            new SoulSyncDownload("downloading", "One More Time", "Daft Punk", "Discovery", 0.5, null),
            new SoulSyncDownload("downloading", "Aerodynamic", "Daft Punk", "Discovery", 1.0, null),
            new SoulSyncDownload("downloading", "Other", "Someone", "Other Album", 0.1, null),
        };
        var activity = RequestsSyncTask.SoulSyncAlbumActivity(album, downloading);
        Assert.Equal(RequestStatus.Downloading, activity!.Value.Status);
        Assert.Equal(0.75, activity.Value.Progress!.Value, 3);

        var importing = new[] { new SoulSyncDownload("post_processing", "One More Time", "Daft Punk", "Discovery", 1, null) };
        Assert.Equal(RequestStatus.Importing, RequestsSyncTask.SoulSyncAlbumActivity(album, importing)!.Value.Status);

        Assert.Null(RequestsSyncTask.SoulSyncAlbumActivity(album, Array.Empty<SoulSyncDownload>()));
    }

    private static RequestRecord Row(Guid user, int albumId, string title) => new()
    {
        JellyfinUserId = user,
        LidarrAlbumId = albumId,
        AlbumTitle = title,
        SubmittedAt = T0,
        Status = RequestStatus.Pending,
        StatusUpdatedAt = T0,
    };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
