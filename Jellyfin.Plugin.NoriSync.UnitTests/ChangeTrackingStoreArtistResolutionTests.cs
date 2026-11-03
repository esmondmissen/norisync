using System;
using System.IO;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for the artist-name resolution <see cref="ChangeTrackingStore"/> records between runs:
/// it is what later resolution is compared against, so it must round-trip intact, survive a
/// restart, and tell "never recorded" (which triggers the one-time backlog) apart from "recorded
/// empty".
/// </summary>
public sealed class ChangeTrackingStoreArtistResolutionTests : IDisposable
{
    private readonly string _tempDir;

    public ChangeTrackingStoreArtistResolutionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-unittests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Leave the temp dir on Windows file-lock contention; nothing important leaks.
        }
    }

    [Fact]
    public void NeverRecordedIsNull()
    {
        using var store = new ChangeTrackingStore(_tempDir, NullLogger.Instance);

        Assert.Null(store.GetArtistResolution());
    }

    [Fact]
    public void RecordedEmptyIsEmptyNotNull()
    {
        using var store = new ChangeTrackingStore(_tempDir, NullLogger.Instance);
        store.ReplaceArtistResolution(Array.Empty<ArtistResolutionEntry>());

        Assert.Empty(store.GetArtistResolution()!);
    }

    [Fact]
    public void EntriesRoundTripAcrossARestartAndAreReplacedWhole()
    {
        var lookup = ArtistNameLookup.Build(new[]
        {
            ("Anne‐Marie", Guid.NewGuid(), false),
            ("AC/DC", Guid.NewGuid(), false)
        });

        using (var store = new ChangeTrackingStore(_tempDir, NullLogger.Instance))
        {
            store.ReplaceArtistResolution(new[] { new ArtistResolutionEntry(false, "Stale", Guid.NewGuid()) });
            store.ReplaceArtistResolution(lookup.GetEntries());
        }

        using var reopened = new ChangeTrackingStore(_tempDir, NullLogger.Instance);
        var recorded = reopened.GetArtistResolution()!;

        Assert.Equal(lookup.GetEntries().Count, recorded.Count);
        Assert.Null(lookup.GetChangedCredits(recorded));
    }
}
