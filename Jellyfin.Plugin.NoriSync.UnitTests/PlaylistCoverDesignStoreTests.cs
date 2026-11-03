using System;
using System.IO;
using Jellyfin.Plugin.NoriSync.Api;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="PlaylistCoverDesignStore"/> and the design checks in
/// <see cref="PlaylistImageController"/>. The invariant Nori relies on: a design is served only
/// while its playlist still shows the exact image the design was baked into.
/// </summary>
public sealed class PlaylistCoverDesignStoreTests : IDisposable
{
    private const string Design = "{\"schemaVersion\":1,\"background\":{\"kind\":\"solid\",\"color\":\"#112233\"},\"textBoxes\":[]}";
    private const string ImagePath = "/config/data/playlists/Road Trip/folder.jpg";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 11, 2, 11, 59, 30, 123, DateTimeKind.Utc);

    private readonly string _tempDir;
    private PlaylistCoverDesignStore _store;

    public PlaylistCoverDesignStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-coverdesign-tests-" + Guid.NewGuid().ToString("N"));
        _store = new PlaylistCoverDesignStore(_tempDir, NullLogger.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Leave the temp dir on file-lock contention; nothing important leaks.
        }
    }

    [Fact]
    public void Save_ThenGet_RoundTripsTheDocumentVerbatim()
    {
        var playlist = Guid.NewGuid();
        _store.Save(playlist, Design, ImagePath, Modified, Now);

        var record = _store.Get(playlist);

        Assert.NotNull(record);
        Assert.Equal(playlist, record!.PlaylistId);
        Assert.Equal(Design, record.Design);
        Assert.True(PlaylistCoverDesignStore.Describes(record, ImagePath, Modified));
    }

    [Fact]
    public void Save_Again_ReplacesTheDesignAndItsImage()
    {
        var playlist = Guid.NewGuid();
        _store.Save(playlist, Design, ImagePath, Modified, Now);
        var newer = Modified.AddSeconds(40);
        _store.Save(playlist, "{\"v\":2}", ImagePath, newer, Now.AddSeconds(40));

        var record = _store.Get(playlist);

        Assert.Equal("{\"v\":2}", record!.Design);
        Assert.False(PlaylistCoverDesignStore.Describes(record, ImagePath, Modified));
        Assert.True(PlaylistCoverDesignStore.Describes(record, ImagePath, newer));
        Assert.Single(_store.GetAll());
    }

    [Fact]
    public void Describes_IsFalse_OnceTheCoverChangesElsewhere()
    {
        var playlist = Guid.NewGuid();
        _store.Save(playlist, Design, ImagePath, Modified, Now);
        var record = _store.Get(playlist)!;

        // The web UI overwrites folder.jpg in place: same path, new modification time.
        Assert.False(PlaylistCoverDesignStore.Describes(record, ImagePath, Modified.AddTicks(1)));
        // Another client saved a different file.
        Assert.False(PlaylistCoverDesignStore.Describes(record, "/config/data/playlists/Road Trip/folder.png", Modified));
        // The image was removed.
        Assert.False(PlaylistCoverDesignStore.Describes(record, null, Modified));
    }

    [Fact]
    public void Describes_ComparesTicks_NotDateTimeKind()
    {
        // Jellyfin reloads image dates from its database as Unspecified; the ticks are what match.
        var playlist = Guid.NewGuid();
        _store.Save(playlist, Design, ImagePath, Modified, Now);
        var reloaded = DateTime.SpecifyKind(Modified, DateTimeKind.Unspecified);

        Assert.True(PlaylistCoverDesignStore.Describes(_store.Get(playlist)!, ImagePath, reloaded));
    }

    [Fact]
    public void Delete_RemovesOnlyThatPlaylist()
    {
        var kept = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        _store.Save(kept, Design, ImagePath, Modified, Now);
        _store.Save(dropped, Design, ImagePath, Modified, Now);

        Assert.True(_store.Delete(dropped));
        Assert.False(_store.Delete(dropped));
        Assert.Null(_store.Get(dropped));
        Assert.Equal(new[] { kept }, _store.GetAllItemIds());
    }

    [Fact]
    public void Designs_SurviveARestart()
    {
        var playlist = Guid.NewGuid();
        _store.Save(playlist, Design, ImagePath, Modified, Now);

        _store.Dispose();
        SqliteConnection.ClearAllPools();
        _store = new PlaylistCoverDesignStore(_tempDir, NullLogger.Instance);

        Assert.Equal(Design, _store.Get(playlist)!.Design);
    }

    [Theory]
    [InlineData(Design, true)]
    [InlineData("{}", true)]
    [InlineData("[]", false)]
    [InlineData("\"text\"", false)]
    [InlineData("{not json", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsValidDesign_AcceptsOnlyAJsonObject(string? design, bool expected)
    {
        Assert.Equal(expected, PlaylistImageController.IsValidDesign(design));
    }

    [Fact]
    public void IsValidDesign_RejectsAnOversizedDocument()
    {
        var padding = new string('a', PlaylistCoverDesignStore.MaxDesignLength);
        Assert.False(PlaylistImageController.IsValidDesign("{\"pad\":\"" + padding + "\"}"));
    }

    [Fact]
    public void EntityTag_ChangesWithAnyServedField()
    {
        var id = Guid.NewGuid();
        PlaylistCoverDesignDto Make(string tag, string design) => new() { PlaylistId = id, ImageTag = tag, Design = design };

        var baseline = PlaylistImageController.EntityTag(new[] { Make("t1", Design) });

        Assert.Equal(baseline, PlaylistImageController.EntityTag(new[] { Make("t1", Design) }));
        Assert.NotEqual(baseline, PlaylistImageController.EntityTag(new[] { Make("t2", Design) }));
        Assert.NotEqual(baseline, PlaylistImageController.EntityTag(new[] { Make("t1", "{}") }));
        Assert.NotEqual(baseline, PlaylistImageController.EntityTag(Array.Empty<PlaylistCoverDesignDto>()));
        Assert.StartsWith("\"", baseline, StringComparison.Ordinal);
        Assert.EndsWith("\"", baseline, StringComparison.Ordinal);
    }
}
