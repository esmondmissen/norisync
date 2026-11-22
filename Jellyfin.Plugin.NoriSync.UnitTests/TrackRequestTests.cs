using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Song requests onto SoulSync's wishlist (<see cref="TrackRequestService"/>): the payload it
/// takes, the key it stores the row under, its refusals, and a row shared by users who asked for
/// the same recording. Shapes from SoulSync's <c>api/wishlist.py</c>.
/// </summary>
public sealed class TrackRequestTests : IDisposable
{
    private const string Mbid = "3c8f1a43-0f51-4b5c-a5a7-0f6a1d3f3e01";

    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "norisync-tracks-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    /// <summary>
    /// The payload names its ids as MusicBrainz's (without that SoulSync reads them as Spotify
    /// ids), carries the artists as objects, and gives the album no id (a MusicBrainz album id
    /// there is a release, not the release group the app knows).
    /// </summary>
    [Fact]
    public void TrackData_IsMusicBrainzShaped()
    {
        var data = TrackRequestService.BuildTrackData(Mbid, new TrackRequestHints
        {
            Title = " One More Time ",
            Artist = "Daft Punk",
            Album = "Discovery",
            AlbumType = "Album",
            DurationMs = 320357,
        });

        Assert.Equal(Mbid, (string?)data["id"]);
        Assert.Equal("One More Time", (string?)data["name"]);
        Assert.Equal("Daft Punk", (string?)data["artists"]![0]!["name"]);
        Assert.Equal("Discovery", (string?)data["album"]!["name"]);
        Assert.Equal("album", (string?)data["album"]!["album_type"]);
        Assert.Null(data["album"]!["id"]);
        Assert.Equal(320357, (long?)data["duration_ms"]);
        Assert.Equal("musicbrainz", (string?)data["source"]);
        Assert.Equal("musicbrainz", (string?)data["provider"]);
    }

    /// <summary>
    /// With no album known the song stands as its own single, as SoulSync's wishlist would fill
    /// it in anyway.
    /// </summary>
    [Fact]
    public void TrackData_NoAlbum_IsASingle()
    {
        var data = TrackRequestService.BuildTrackData(Mbid, new TrackRequestHints { Title = "Song", Artist = "Artist" });

        Assert.Equal("Song", (string?)data["album"]!["name"]);
        Assert.Equal("single", (string?)data["album"]!["album_type"]);
        Assert.Equal(0, (long?)data["duration_ms"]);
    }

    /// <summary>
    /// What the app didn't send comes from the MusicBrainz recording: title, first credit,
    /// length, and the album (the hinted release group's release, else an album).
    /// </summary>
    [Fact]
    public void MergeRecording_FillsTheGaps()
    {
        const string recording = """
            {
              "id": "3c8f1a43-0f51-4b5c-a5a7-0f6a1d3f3e01", "title": "One More Time", "length": 320357,
              "artist-credit": [ { "name": "Daft Punk", "joinphrase": "" } ],
              "releases": [
                { "title": "One More Time (single)", "release-group": { "id": "aaaaaaaa-0000-0000-0000-000000000001", "primary-type": "Single" } },
                { "title": "Discovery", "release-group": { "id": "bbbbbbbb-0000-0000-0000-000000000002", "primary-type": "Album" } }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(recording);

        var merged = TrackRequestService.MergeRecording(new TrackRequestHints(), doc.RootElement)!;
        Assert.Equal("One More Time", merged.Title);
        Assert.Equal("Daft Punk", merged.Artist);
        Assert.Equal(320357, merged.DurationMs);
        Assert.Equal("Discovery", merged.Album);
        Assert.Equal("Album", merged.AlbumType);

        var hinted = TrackRequestService.MergeRecording(new TrackRequestHints { ReleaseGroupMbid = "aaaaaaaa-0000-0000-0000-000000000001" }, doc.RootElement)!;
        Assert.Equal("One More Time (single)", hinted.Album);
    }

    /// <summary>
    /// A recording id is a MusicBrainz id, stored lower-case.
    /// </summary>
    [Fact]
    public void NormalizeMbid_AcceptsOnlyMusicBrainzIds()
    {
        Assert.Equal(Mbid, TrackRequestService.NormalizeMbid(Mbid.ToUpperInvariant()));
        Assert.Null(TrackRequestService.NormalizeMbid("lr:track:x"));
        Assert.Null(TrackRequestService.NormalizeMbid(null));
    }

    /// <summary>
    /// A request goes on the wishlist, is kept under the key SoulSync answers with (it may
    /// differ from the id sent), and starts a wishlist run.
    /// </summary>
    [Fact]
    public async Task Request_AddsToTheWishlistAndKeepsTheStoredKey()
    {
        var fake = new FakeSoulSync();
        fake.On("POST wishlist", HttpStatusCode.Created, FakeSoulSync.Ok("""{ "message": "Track added to wishlist.", "created": true, "track": { "track_id": "canonical-key", "track_name": "One More Time" } }"""));
        fake.On("POST wishlist/process", HttpStatusCode.OK, FakeSoulSync.Ok("""{ "message": "Wishlist processing started." }"""));
        using var store = NewStore();
        var service = NewService(fake, store);
        var user = Guid.NewGuid();

        var outcome = await service.RequestAsync(user, Mbid, Hints(), T0, CancellationToken.None);

        Assert.Equal(TrackRequestOutcomeKind.Requested, outcome.Kind);
        Assert.Equal("canonical-key", outcome.Record!.SoulSyncTrackId);
        Assert.Equal(TrackRequestStatus.Queued, outcome.Record.Status);
        var body = JsonNode.Parse(Assert.Single(fake.Requests("POST", "wishlist")).Body)!;
        Assert.Equal(Mbid, (string?)body["track_data"]!["id"]);
        Assert.Equal("nori", (string?)body["source_type"]);
        Assert.Single(fake.Requests("POST", "wishlist/process"));
        Assert.Equal("1", fake.LastProfileId);
    }

    /// <summary>
    /// A wishlist run already going (409) is fine: it picks the song up.
    /// </summary>
    [Fact]
    public async Task Request_WishlistRunAlreadyGoing_StillRequested()
    {
        var fake = new FakeSoulSync();
        fake.On("POST wishlist", HttpStatusCode.OK, FakeSoulSync.Ok("""{ "created": false, "track": { "track_id": "3c8f1a43-0f51-4b5c-a5a7-0f6a1d3f3e01" } }"""));
        fake.On("POST wishlist/process", HttpStatusCode.Conflict, FakeSoulSync.Error("CONFLICT", "Wishlist processing is already running."));
        using var store = NewStore();

        var outcome = await NewService(fake, store).RequestAsync(Guid.NewGuid(), Mbid, Hints(), T0, CancellationToken.None);

        Assert.Equal(TrackRequestOutcomeKind.Requested, outcome.Kind);
    }

    /// <summary>
    /// SoulSync's refusal (400: the profile's request limit) is the user's to read; nothing is
    /// stored.
    /// </summary>
    [Fact]
    public async Task Request_RequestLimit_IsRefusedWithAReason()
    {
        var fake = new FakeSoulSync();
        fake.On("POST wishlist", HttpStatusCode.BadRequest, FakeSoulSync.Error("BAD_REQUEST", "request_limit"));
        using var store = NewStore();

        var outcome = await NewService(fake, store).RequestAsync(Guid.NewGuid(), Mbid, Hints(), T0, CancellationToken.None);

        Assert.Equal(TrackRequestOutcomeKind.Refused, outcome.Kind);
        Assert.Contains("request limit", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(store.ListAll());
        Assert.Empty(fake.Requests("POST", "wishlist/process"));
    }

    /// <summary>
    /// SoulSync wrote nothing (409: it already has the song): not added, nothing stored.
    /// </summary>
    [Fact]
    public async Task Request_Conflict_IsNotAdded()
    {
        var fake = new FakeSoulSync();
        fake.On("POST wishlist", HttpStatusCode.Conflict, FakeSoulSync.Error("CONFLICT", "manual library match"));
        using var store = NewStore();

        var outcome = await NewService(fake, store).RequestAsync(Guid.NewGuid(), Mbid, Hints(), T0, CancellationToken.None);

        Assert.Equal(TrackRequestOutcomeKind.NotAdded, outcome.Kind);
        Assert.Equal("manual library match", outcome.Message);
        Assert.Empty(store.ListAll());
    }

    /// <summary>
    /// Without a title and artist, and nowhere to read them from, there is no song to ask for.
    /// </summary>
    [Fact]
    public async Task Request_NothingKnown_IsUnknown()
    {
        var fake = new FakeSoulSync();
        using var store = NewStore();

        var outcome = await NewService(fake, store).RequestAsync(Guid.NewGuid(), Mbid, new TrackRequestHints(), T0, CancellationToken.None);

        Assert.Equal(TrackRequestOutcomeKind.UnknownRecording, outcome.Kind);
        Assert.Empty(fake.All());
    }

    /// <summary>
    /// Two users asking for one song share SoulSync's row: the first to cancel leaves it, the
    /// last takes it off the wishlist.
    /// </summary>
    [Fact]
    public async Task Cancel_SharedRowLeavesTheWishlistWithTheLastRequest()
    {
        var fake = new FakeSoulSync();
        fake.On("POST wishlist", HttpStatusCode.Created, FakeSoulSync.Ok("""{ "created": true, "track": { "track_id": "key-1" } }"""));
        fake.On("POST wishlist/process", HttpStatusCode.OK, FakeSoulSync.Ok("{}"));
        fake.On("DELETE wishlist/key-1", HttpStatusCode.OK, FakeSoulSync.Ok("""{ "message": "Track removed from wishlist." }"""));
        using var store = NewStore();
        var service = NewService(fake, store);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await service.RequestAsync(alice, Mbid, Hints(), T0, CancellationToken.None);
        await service.RequestAsync(bob, Mbid, Hints(), T0, CancellationToken.None);

        Assert.True(await service.CancelAsync(alice, Mbid, everyone: false, CancellationToken.None));
        Assert.Empty(fake.Requests("DELETE", "wishlist/key-1"));
        Assert.Single(store.ListByRecording(Mbid));

        Assert.True(await service.CancelAsync(bob, Mbid, everyone: false, CancellationToken.None));
        Assert.Single(fake.Requests("DELETE", "wishlist/key-1"));
        Assert.Empty(store.ListAll());
    }

    /// <summary>
    /// An admin's cancel withdraws everyone's, and nobody can cancel what they never asked for.
    /// </summary>
    [Fact]
    public async Task Cancel_AdminWithdrawsEveryonesAndOthersFindNothing()
    {
        var fake = new FakeSoulSync();
        fake.On("POST wishlist", HttpStatusCode.Created, FakeSoulSync.Ok("""{ "created": true, "track": { "track_id": "key-1" } }"""));
        fake.On("POST wishlist/process", HttpStatusCode.OK, FakeSoulSync.Ok("{}"));
        fake.On("DELETE wishlist/key-1", HttpStatusCode.NotFound, FakeSoulSync.Error("NOT_FOUND", "Track not found in wishlist."));
        using var store = NewStore();
        var service = NewService(fake, store);
        await service.RequestAsync(Guid.NewGuid(), Mbid, Hints(), T0, CancellationToken.None);
        await service.RequestAsync(Guid.NewGuid(), Mbid, Hints(), T0, CancellationToken.None);

        Assert.False(await service.CancelAsync(Guid.NewGuid(), Mbid, everyone: false, CancellationToken.None));
        Assert.True(await service.CancelAsync(Guid.NewGuid(), Mbid, everyone: true, CancellationToken.None));
        Assert.Empty(store.ListAll());
        Assert.Single(fake.Requests("DELETE", "wishlist/key-1"));
    }

    private static TrackRequestHints Hints() => new() { Title = "One More Time", Artist = "Daft Punk", Album = "Discovery" };

    private static TrackRequestService NewService(FakeSoulSync fake, TrackRequestsStore store)
        => new(FakeSoulSync.Client(fake), null, () => store, NullLogger<TrackRequestService>.Instance);

    private TrackRequestsStore NewStore() => new(_dataPath, NullLogger.Instance);
}
