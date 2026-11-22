using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// The song request sweep (<see cref="TrackRequestsSync"/>): the library first, then SoulSync's
/// download task for the song, then its wishlist row, then the grace before a song that left the
/// wishlist reads as failed. Shapes from SoulSync's <c>api/downloads.py</c> and
/// <c>api/serializers.py</c>.
/// </summary>
public sealed class TrackRequestsSyncTests : IDisposable
{
    private const string Mbid = "3c8f1a43-0f51-4b5c-a5a7-0f6a1d3f3e01";

    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, TrackRequestsSync.WishlistEntry> NoWishlist = new();

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "norisync-tracksync-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    /// <summary>
    /// A download task for the song decides: downloading with its progress, importing while it
    /// is post-processed, failed with its error.
    /// </summary>
    /// <param name="status">The task's status.</param>
    /// <param name="expected">The request's status.</param>
    [Theory]
    [InlineData("downloading", TrackRequestStatus.Downloading)]
    [InlineData("post_processing", TrackRequestStatus.Importing)]
    [InlineData("completed", TrackRequestStatus.Importing)]
    [InlineData("searching", TrackRequestStatus.Searching)]
    [InlineData("queued", TrackRequestStatus.Searching)]
    [InlineData("failed", TrackRequestStatus.Failed)]
    [InlineData("not_found", TrackRequestStatus.Failed)]
    public void Next_DownloadTaskDecides(string status, TrackRequestStatus expected)
    {
        var task = new SoulSyncDownload(status, "One More Time (Radio Edit)", "Daft Punk", "Discovery", 0.4, status == "failed" ? "peer went away" : null);
        var row = Row(title: "One More Time (Radio Edit)");

        var next = TrackRequestsSync.Next(row, NoWishlist, new[] { task }, T0);

        Assert.Equal(expected, next.Status);
        Assert.Equal(expected == TrackRequestStatus.Downloading ? 0.4 : null, next.Progress);
        if (status == "failed")
        {
            Assert.Equal("peer went away", next.Error);
        }
    }

    /// <summary>
    /// A cancelled task and another song's task are ignored.
    /// </summary>
    [Fact]
    public void Next_IgnoresCancelledAndOtherSongs()
    {
        var wishlist = new Dictionary<string, TrackRequestsSync.WishlistEntry> { [Mbid] = new(Mbid, 0, null, null) };
        var tasks = new[]
        {
            new SoulSyncDownload("cancelled", "One More Time", "Daft Punk", null, 0, null),
            new SoulSyncDownload("downloading", "Digital Love", "Daft Punk", null, 0.9, null),
        };

        Assert.Equal(TrackRequestStatus.Queued, TrackRequestsSync.Next(Row(), wishlist, tasks, T0).Status);
    }

    /// <summary>
    /// On the wishlist with no task, the song waits for SoulSync's next run, showing why the last
    /// try failed.
    /// </summary>
    [Fact]
    public void Next_OnTheWishlist_IsQueuedWithTheLastFailure()
    {
        var tried = T0.AddMinutes(-30);
        var wishlist = new Dictionary<string, TrackRequestsSync.WishlistEntry> { [Mbid] = new(Mbid, 2, tried, "No match found") };

        var next = TrackRequestsSync.Next(Row(), wishlist, Array.Empty<SoulSyncDownload>(), T0);

        Assert.Equal(TrackRequestStatus.Queued, next.Status);
        Assert.Equal("No match found", next.Error);
        Assert.Equal(2, next.RetryCount);
        Assert.Equal(tried, next.LastAttemptAt);
        Assert.Null(next.MissingSince);
    }

    /// <summary>
    /// SoulSync drops the row once the file is published, before Jellyfin has scanned it: off the
    /// wishlist it reads as importing for the grace, then as failed.
    /// </summary>
    [Fact]
    public void Next_OffTheWishlist_ImportingThenFailed()
    {
        var first = TrackRequestsSync.Next(Row(), NoWishlist, Array.Empty<SoulSyncDownload>(), T0);
        Assert.Equal(TrackRequestStatus.Importing, first.Status);
        Assert.Equal(T0, first.MissingSince);

        var row = Row();
        row.WishlistMissingSince = T0;
        Assert.Equal(TrackRequestStatus.Importing, TrackRequestsSync.Next(row, NoWishlist, Array.Empty<SoulSyncDownload>(), T0.AddMinutes(59)).Status);

        var late = TrackRequestsSync.Next(row, NoWishlist, Array.Empty<SoulSyncDownload>(), T0 + TrackRequestsSync.MissingGrace);
        Assert.Equal(TrackRequestStatus.Failed, late.Status);
        Assert.Equal(T0, late.MissingSince);
    }

    /// <summary>
    /// The row is found under the key SoulSync stored it under, not the recording id.
    /// </summary>
    [Fact]
    public void Next_FindsTheRowByItsStoredKey()
    {
        var row = Row();
        row.SoulSyncTrackId = "canonical-key";
        var wishlist = new Dictionary<string, TrackRequestsSync.WishlistEntry> { ["canonical-key"] = new("canonical-key", 0, null, null) };

        Assert.Equal(TrackRequestStatus.Queued, TrackRequestsSync.Next(row, wishlist, Array.Empty<SoulSyncDownload>(), T0).Status);
    }

    /// <summary>
    /// A song in the library is done: every request for it goes. The rest are moved on from one
    /// wishlist read.
    /// </summary>
    [Fact]
    public async Task Sync_LibraryFirstThenOneWishlistRead()
    {
        using var store = new TrackRequestsStore(_dataPath, NullLogger.Instance);
        const string other = "11111111-2222-3333-4444-555555555555";
        store.Upsert(Row());
        store.Upsert(Row(user: Guid.NewGuid()));
        store.Upsert(Row(mbid: other, title: "Digital Love"));
        var fake = new FakeSoulSync();
        fake.On("GET wishlist", HttpStatusCode.OK, FakeSoulSync.Ok("""{ "tracks": [ { "track_id": "11111111-2222-3333-4444-555555555555", "retry_count": 1, "failure_reason": "No match found", "last_attempted": "2026-11-20T11:30:00" } ] }"""));
        var library = new FakeLibrary(Mbid);
        var sync = new TrackRequestsSync(FakeSoulSync.Client(fake), library, NullLogger<TrackRequestsSync>.Instance);

        await sync.SyncAsync(store, Array.Empty<SoulSyncDownload>(), T0, CancellationToken.None);

        Assert.Empty(store.ListByRecording(Mbid));
        var left = Assert.Single(store.ListAll());
        Assert.Equal(TrackRequestStatus.Queued, left.Status);
        Assert.Equal("No match found", left.LastError);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 11, 30, 0, TimeSpan.Zero), left.LastAttemptAt);
        Assert.Single(fake.Requests("GET", "wishlist"));
    }

    /// <summary>
    /// A wishlist that can't be read would read as "every song left it": nothing changes.
    /// </summary>
    [Fact]
    public async Task Sync_UnreadableWishlist_ChangesNothing()
    {
        using var store = new TrackRequestsStore(_dataPath, NullLogger.Instance);
        store.Upsert(Row());
        var fake = new FakeSoulSync();
        fake.On("GET wishlist", HttpStatusCode.InternalServerError, FakeSoulSync.Error("WISHLIST_ERROR", "boom"));
        var sync = new TrackRequestsSync(FakeSoulSync.Client(fake), new FakeLibrary(), NullLogger<TrackRequestsSync>.Instance);

        await sync.SyncAsync(store, Array.Empty<SoulSyncDownload>(), T0, CancellationToken.None);

        var row = Assert.Single(store.ListAll());
        Assert.Equal(TrackRequestStatus.Searching, row.Status);
        Assert.Null(row.WishlistMissingSince);
    }

    /// <summary>
    /// The name rule: punctuation and case don't matter, an artist inside a credit line counts,
    /// another title doesn't.
    /// </summary>
    /// <param name="title">The candidate's title.</param>
    /// <param name="artist">The candidate's artist.</param>
    /// <param name="expected">Whether it is the requested song.</param>
    [Theory]
    [InlineData("One More Time", "Daft Punk", true)]
    [InlineData("one more time!", "DAFT PUNK", true)]
    [InlineData("One More Time", "Daft Punk, Romanthony", true)]
    [InlineData("One More Time (Live)", "Daft Punk", false)]
    [InlineData("One More Time", "Britney Spears", false)]
    [InlineData("", "Daft Punk", false)]
    public void SameSong_FoldsAndContains(string title, string artist, bool expected)
        => Assert.Equal(expected, TrackMatch.SameSong(title, new[] { artist }, "One More Time", "Daft Punk"));

    private static TrackRequestRecord Row(string mbid = Mbid, string title = "One More Time", Guid? user = null) => new()
    {
        JellyfinUserId = user ?? Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        RecordingMbid = mbid,
        SoulSyncTrackId = mbid,
        Title = title,
        ArtistName = "Daft Punk",
        SubmittedAt = T0.AddHours(-1),
        Status = TrackRequestStatus.Searching,
        StatusUpdatedAt = T0.AddHours(-1),
    };

    private sealed class FakeLibrary : ITrackLibrary
    {
        private readonly HashSet<string> _have;

        public FakeLibrary(params string[] have)
        {
            _have = new HashSet<string>(have, StringComparer.OrdinalIgnoreCase);
        }

        public bool Contains(string recordingMbid, string? title, string? artist, DateTimeOffset since) => _have.Contains(recordingMbid);
    }
}
