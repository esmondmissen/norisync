using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// The plugin's own searches for requested albums (<see cref="AlbumSearchScheduler"/>, driven by
/// <see cref="RequestsSyncTask"/>), against a stand-in Lidarr: no search before the album's
/// releases load, the result read back from the command, and the backoff after a miss.
/// </summary>
public sealed class AlbumSearchSchedulerTests : IDisposable
{
    private const string NotLoaded = """{ "id": 7, "title": "A", "releases": [ { "duration": 0, "monitored": true } ], "statistics": { "trackFileCount": 0, "totalTrackCount": 0 } }""";

    private const string Loaded = """{ "id": 7, "title": "A", "releases": [ { "duration": 2400000, "monitored": true } ], "statistics": { "trackFileCount": 0, "totalTrackCount": 10 } }""";

    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "norisync-search-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    /// <summary>
    /// A search sent before Lidarr has the album's releases rejects every sized result, so the
    /// schedule waits and sends nothing.
    /// </summary>
    [Fact]
    public async Task Advance_ReleasesNotLoaded_WaitsWithoutSearching()
    {
        var lidarr = new FakeLidarr();
        using var scheduler = NewScheduler(lidarr);

        var (record, searched) = await scheduler.AdvanceAsync(AlbumSearchScheduler.Fresh(7, T0), Json(NotLoaded), T0.AddMinutes(1), true, CancellationToken.None);

        Assert.False(searched);
        Assert.Equal(AlbumSearchState.AwaitingMetadata, record.State);
        Assert.Equal(0, record.Attempts);
        Assert.Empty(lidarr.Posts("command"));
    }

    /// <summary>
    /// Once a release has a length the search goes out, and the command id is kept to read
    /// its result.
    /// </summary>
    [Fact]
    public async Task Advance_ReleasesLoaded_SendsAlbumSearch()
    {
        var lidarr = new FakeLidarr { CommandId = 42 };
        using var scheduler = NewScheduler(lidarr);

        var (record, searched) = await scheduler.AdvanceAsync(AlbumSearchScheduler.Fresh(7, T0), Json(Loaded), T0.AddMinutes(1), true, CancellationToken.None);

        Assert.True(searched);
        Assert.Equal(AlbumSearchState.Searching, record.State);
        Assert.Equal(42, record.CommandId);
        Assert.Equal(1, record.Attempts);
        Assert.Equal(T0.AddMinutes(1), record.LastSearchedAt);
        Assert.Null(record.NextSearchAt);
        var body = Assert.Single(lidarr.Posts("command"));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("AlbumSearch", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("albumIds")[0].GetInt32());
    }

    /// <summary>
    /// An album whose releases never get a length (MusicBrainz has no durations) is searched
    /// anyway once the patience runs out, rather than never.
    /// </summary>
    [Fact]
    public async Task Advance_ReleasesNeverLoad_SearchesAfterPatience()
    {
        var lidarr = new FakeLidarr();
        using var scheduler = NewScheduler(lidarr);

        var later = T0 + AlbumSearchScheduler.MetadataPatience;
        var (record, searched) = await scheduler.AdvanceAsync(AlbumSearchScheduler.Fresh(7, T0), Json(NotLoaded), later, true, CancellationToken.None);

        Assert.True(searched);
        Assert.Equal(AlbumSearchState.Searching, record.State);
    }

    /// <summary>
    /// A sweep that has used its share of searches leaves a due album for the next one.
    /// </summary>
    [Fact]
    public async Task Advance_NoSearchesLeftThisSweep_SendsNothing()
    {
        var lidarr = new FakeLidarr();
        using var scheduler = NewScheduler(lidarr);

        var (record, searched) = await scheduler.AdvanceAsync(AlbumSearchScheduler.Fresh(7, T0), Json(Loaded), T0, false, CancellationToken.None);

        Assert.False(searched);
        Assert.Equal(T0, record.NextSearchAt);
        Assert.Empty(lidarr.Posts("command"));
    }

    /// <summary>
    /// Lidarr reports an AlbumSearch that found nothing as a successful command; only its
    /// message says "0 reports downloaded". That is a miss, searched again after the backoff.
    /// </summary>
    [Fact]
    public async Task Advance_SearchFoundNothing_SchedulesTheNextSearch()
    {
        var lidarr = new FakeLidarr();
        lidarr.Commands[42] = """{ "id": 42, "status": "completed", "result": "successful", "message": "Album search completed. 0 reports downloaded." }""";
        using var scheduler = NewScheduler(lidarr);
        var searching = Searching(attempts: 1);

        var now = T0.AddMinutes(3);
        var (record, searched) = await scheduler.AdvanceAsync(searching, Json(Loaded), now, true, CancellationToken.None);

        Assert.False(searched);
        Assert.Equal(AlbumSearchState.NothingFound, record.State);
        Assert.Null(record.CommandId);
        Assert.Equal(now + TimeSpan.FromMinutes(15), record.NextSearchAt);
        Assert.Equal("Album search completed. 0 reports downloaded.", record.LastMessage);
        Assert.Empty(lidarr.Posts("command"));
    }

    /// <summary>
    /// A search that sent a release reads as grabbed.
    /// </summary>
    [Fact]
    public async Task Advance_SearchGrabbed_ReadsAsGrabbed()
    {
        var lidarr = new FakeLidarr();
        lidarr.Commands[42] = """{ "id": 42, "status": "completed", "message": "Album search completed. 1 reports downloaded." }""";
        using var scheduler = NewScheduler(lidarr);

        var (record, _) = await scheduler.AdvanceAsync(Searching(attempts: 1), Json(Loaded), T0.AddMinutes(3), true, CancellationToken.None);

        Assert.Equal(AlbumSearchState.Grabbed, record.State);
    }

    /// <summary>
    /// A search still running is left alone; one Lidarr has forgotten (404 once purged) or
    /// that failed counts as a miss, never as a hang.
    /// </summary>
    /// <param name="command">The command as Lidarr returns it, or null for a 404.</param>
    /// <param name="expected">The state the schedule should be in afterwards.</param>
    [Theory]
    [InlineData("""{ "id": 42, "status": "started", "message": "Searching indexers" }""", AlbumSearchState.Searching)]
    [InlineData("""{ "id": 42, "status": "queued" }""", AlbumSearchState.Searching)]
    [InlineData("""{ "id": 42, "status": "failed", "message": "boom" }""", AlbumSearchState.NothingFound)]
    [InlineData(null, AlbumSearchState.NothingFound)]
    public async Task Advance_SearchInFlight_ReadsTheCommand(string? command, AlbumSearchState expected)
    {
        var lidarr = new FakeLidarr();
        if (command != null)
        {
            lidarr.Commands[42] = command;
        }

        using var scheduler = NewScheduler(lidarr);

        var (record, searched) = await scheduler.AdvanceAsync(Searching(attempts: 1), Json(Loaded), T0.AddMinutes(3), true, CancellationToken.None);

        Assert.False(searched);
        Assert.Equal(expected, record.State);
        Assert.Empty(lidarr.Posts("command"));
    }

    /// <summary>
    /// The backoff after each search, ending once the next would fall past the retry window.
    /// </summary>
    [Fact]
    public void NextAfter_BacksOffThenGivesUp()
    {
        var record = AlbumSearchScheduler.Fresh(7, T0);

        record.Attempts = 1;
        Assert.Equal(T0 + TimeSpan.FromMinutes(15), AlbumSearchScheduler.NextAfter(record, T0));
        record.Attempts = 2;
        Assert.Equal(T0 + TimeSpan.FromHours(1), AlbumSearchScheduler.NextAfter(record, T0));
        record.Attempts = 3;
        Assert.Equal(T0 + TimeSpan.FromHours(6), AlbumSearchScheduler.NextAfter(record, T0));
        record.Attempts = 9;
        Assert.Equal(T0 + TimeSpan.FromHours(24), AlbumSearchScheduler.NextAfter(record, T0));

        var nearTheEnd = T0 + AlbumSearchScheduler.RetryWindow - TimeSpan.FromHours(1);
        Assert.Null(AlbumSearchScheduler.NextAfter(record, nearTheEnd));
    }

    /// <summary>
    /// The completion line is read for its count; anything else is no count.
    /// </summary>
    /// <param name="message">The command message.</param>
    /// <param name="expected">The count, or null.</param>
    [Theory]
    [InlineData("Album search completed. 0 reports downloaded.", 0)]
    [InlineData("Album search completed. 3 reports downloaded.", 3)]
    [InlineData("Searching indexers for [Artist - Album]", null)]
    [InlineData(null, null)]
    public void ReportsDownloaded_ParsesTheCompletionLine(string? message, int? expected)
        => Assert.Equal(expected, AlbumSearchScheduler.ReportsDownloaded(message));

    /// <summary>
    /// A new artist created by a request monitors only the requested album and runs no
    /// artist-wide search.
    /// </summary>
    [Fact]
    public void NewArtistAddOptions_MonitorOnlyTheRequestedAlbum()
    {
        var options = LidarrController.BuildNewArtistAddOptions("rg-1");

        Assert.Equal("rg-1", (string?)options["albumsToMonitor"]![0]);
        Assert.Single(options["albumsToMonitor"]!.AsArray());
        Assert.False((bool)options["searchForMissingAlbums"]!);
        Assert.NotEqual("none", (string?)options["monitor"]);
    }

    /// <summary>
    /// A sweep searches a pending request (one from before the schedule existed included)
    /// once its releases load, and never one Lidarr is downloading.
    /// </summary>
    [Fact]
    public async Task Sweep_SearchesPendingAlbumsButNotDownloadingOnes()
    {
        using var store = NewStore();
        AddRequest(store, albumId: 7);
        AddRequest(store, albumId: 8);
        var lidarr = new FakeLidarr();
        lidarr.Albums[7] = Loaded;
        lidarr.Albums[8] = Loaded.Replace("\"id\": 7", "\"id\": 8", StringComparison.Ordinal);
        lidarr.QueueAlbumIds.Add(8);
        using var scheduler = NewScheduler(lidarr);
        var task = NewTask(lidarr, scheduler);

        await task.SyncAsync(store, T0, CancellationToken.None);

        var body = Assert.Single(lidarr.Posts("command"));
        Assert.Contains("[7]", body.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        var rows = store.ListAll().ToDictionary(r => r.LidarrAlbumId);
        Assert.Equal(AlbumSearchState.Searching, rows[7].SearchState);
        Assert.Equal(1, rows[7].SearchAttempts);
        Assert.Null(rows[8].SearchState);
        Assert.Equal(RequestStatus.Downloading, rows[8].Status);
    }

    /// <summary>
    /// A backlog is searched a few albums per sweep, not all at once.
    /// </summary>
    [Fact]
    public async Task Sweep_CapsSearchesPerSweep()
    {
        using var store = NewStore();
        var lidarr = new FakeLidarr();
        for (var id = 1; id <= AlbumSearchScheduler.SearchesPerSweep + 3; id++)
        {
            AddRequest(store, id);
            lidarr.Albums[id] = Loaded.Replace("\"id\": 7", "\"id\": " + id, StringComparison.Ordinal);
        }

        using var scheduler = NewScheduler(lidarr);
        var task = NewTask(lidarr, scheduler);

        await task.SyncAsync(store, T0, CancellationToken.None);
        Assert.Equal(AlbumSearchScheduler.SearchesPerSweep, lidarr.Posts("command").Count);

        await task.SyncAsync(store, T0.AddMinutes(1), CancellationToken.None);
        Assert.Equal(AlbumSearchScheduler.SearchesPerSweep + 3, lidarr.Posts("command").Count);
    }

    /// <summary>
    /// The schedule belongs to the album: it stays while another user still requests it, and
    /// goes with the last request.
    /// </summary>
    [Fact]
    public void Store_ScheduleGoesWithTheLastRequest()
    {
        using var store = NewStore();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        AddRequest(store, 7, alice);
        AddRequest(store, 7, bob);
        store.UpsertSearch(AlbumSearchScheduler.Fresh(7, T0));

        store.Delete(alice, 7);
        Assert.NotNull(store.GetSearch(7));
        Assert.Equal(AlbumSearchState.AwaitingMetadata, Assert.Single(store.ListByUser(bob)).SearchState);

        store.Delete(bob, 7);
        Assert.Null(store.GetSearch(7));

        // A sweep finishing after the cancel can't bring it back.
        store.UpsertSearch(AlbumSearchScheduler.Fresh(7, T0));
        Assert.Null(store.GetSearch(7));
    }

    /// <summary>
    /// "Search Again" while a sweep holds an older copy of the schedule: the sweep reads the
    /// stored schedule under the lock, so it moves on from the restart instead of writing the
    /// older copy back (and sending a second search).
    /// </summary>
    [Fact]
    public async Task Sweep_MovesOnFromARestartNotAnOlderCopy()
    {
        using var store = NewStore();
        AddRequest(store, 7);
        var lidarr = new FakeLidarr();
        lidarr.Albums[7] = Loaded;
        using var scheduler = NewScheduler(lidarr);
        var gaveUp = AlbumSearchScheduler.Fresh(7, T0);
        gaveUp.State = AlbumSearchState.NothingFound;
        gaveUp.NextSearchAt = null;
        store.UpsertSearch(gaveUp);

        var now = T0.AddDays(20);
        await scheduler.RestartAsync(store, AlbumSearchScheduler.Fresh(7, now), CancellationToken.None);
        await NewTask(lidarr, scheduler).SyncAsync(store, now, CancellationToken.None);

        Assert.Single(lidarr.Posts("command"));
        var search = store.GetSearch(7)!;
        Assert.Equal(AlbumSearchState.Searching, search.State);
        Assert.Equal(now, search.WindowStart);
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static AlbumSearchRecord Searching(int attempts) => new()
    {
        LidarrAlbumId = 7,
        State = AlbumSearchState.Searching,
        Attempts = attempts,
        WindowStart = T0,
        LastSearchedAt = T0,
        CommandId = 42,
    };

    private static void AddRequest(RequestsStore store, int albumId, Guid? user = null)
        => store.Upsert(new RequestRecord
        {
            JellyfinUserId = user ?? Guid.NewGuid(),
            LidarrAlbumId = albumId,
            AlbumMbid = "rg-" + albumId,
            AlbumTitle = "Album " + albumId,
            SubmittedAt = T0,
            Status = RequestStatus.Pending,
            StatusUpdatedAt = T0,
        });

    private static LidarrClient NewClient(FakeLidarr lidarr)
        => new(NullLogger<LidarrClient>.Instance, lidarr, new Uri("http://lidarr:8686/"), "key");

    private static AlbumSearchScheduler NewScheduler(FakeLidarr lidarr)
        => new(NewClient(lidarr), NullLogger<AlbumSearchScheduler>.Instance);

    private static RequestsSyncTask NewTask(FakeLidarr lidarr, AlbumSearchScheduler scheduler)
        => new(NewClient(lidarr), scheduler, NullLogger<RequestsSyncTask>.Instance);

    private RequestsStore NewStore() => new(_dataPath, NullLogger.Instance);

    /// <summary>
    /// A stand-in for the parts of Lidarr's API the request sync uses.
    /// </summary>
    private sealed class FakeLidarr : HttpMessageHandler
    {
        private readonly List<(string Method, string Path, string Body)> _requests = new();

        public Dictionary<int, string> Albums { get; } = new();

        public Dictionary<int, string> Commands { get; } = new();

        public HashSet<int> QueueAlbumIds { get; } = new();

        public int CommandId { get; set; } = 100;

        public List<string> Posts(string path)
        {
            lock (_requests)
            {
                return _requests.Where(r => r.Method == "POST" && r.Path == path).Select(r => r.Body).ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath["/api/v1/".Length..];
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (_requests)
            {
                _requests.Add((request.Method.Method, path, body));
            }

            if (request.Method == HttpMethod.Post && path == "command")
            {
                return Respond(HttpStatusCode.Created, "{ \"id\": " + CommandId++ + ", \"status\": \"queued\" }");
            }

            if (path.StartsWith("command/", StringComparison.Ordinal))
            {
                var id = int.Parse(path["command/".Length..], System.Globalization.CultureInfo.InvariantCulture);
                return Commands.TryGetValue(id, out var command)
                    ? Respond(HttpStatusCode.OK, command)
                    : Respond(HttpStatusCode.NotFound, string.Empty);
            }

            if (path == "queue")
            {
                var records = string.Join(",", QueueAlbumIds.Select(id => "{ \"albumId\": " + id + ", \"size\": 100, \"sizeleft\": 50, \"trackedDownloadState\": \"downloading\" }"));
                return Respond(HttpStatusCode.OK, "{ \"records\": [" + records + "] }");
            }

            if (path == "album")
            {
                var ids = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => p.StartsWith("albumIds=", StringComparison.Ordinal))
                    .Select(p => int.Parse(p["albumIds=".Length..], System.Globalization.CultureInfo.InvariantCulture));
                return Respond(HttpStatusCode.OK, "[" + string.Join(",", ids.Where(Albums.ContainsKey).Select(id => Albums[id])) + "]");
            }

            return Respond(HttpStatusCode.NotFound, string.Empty);
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
