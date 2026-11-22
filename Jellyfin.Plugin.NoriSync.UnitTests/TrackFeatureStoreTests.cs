using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Round-trip and freshness-semantics tests for <see cref="TrackFeatureStore"/>. Lock in the
/// invariants the waveform feed depends on: waveform rows must survive a round trip,
/// <see cref="TrackFeatureStore.HasFreshWaveform"/> must reflect blob presence and version/mtime
/// match, and an old database carrying the removed embedding columns must migrate cleanly.
/// </summary>
public sealed class TrackFeatureStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TrackFeatureStore _store;

    public TrackFeatureStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-unittests-" + Guid.NewGuid().ToString("N"));
        _store = new TrackFeatureStore(_tempDir, NullLogger.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
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
    public void Upsert_FullRow_RoundtripsViaGet()
    {
        var libraryId = Guid.NewGuid();
        var feature = MakeFeature(
            waveform: new byte[] { 1, 2, 3, 4 },
            waveformVersion: "waveform-v5.6");

        _store.Upsert(feature, new[] { libraryId });

        var loaded = _store.Get(feature.ItemId);
        Assert.NotNull(loaded);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, loaded!.Waveform);
        Assert.Equal("waveform-v5.6", loaded.WaveformVersion);
        Assert.Equal(feature.SourceMtime, loaded.SourceMtime);
    }

    [Fact]
    public void Delete_RemovesTheRow_AndItsLibraryJoinRows()
    {
        // Delete had no callers at all until the orphan cleanup was wired up, so nothing ever
        // exercised it. It also only cleared TrackFeatures, leaving a TrackFeatureLibraries row
        // per library the track belonged to — invisible orphans the scoped feed still filters on.
        var libraryA = Guid.NewGuid();
        var libraryB = Guid.NewGuid();
        var feature = MakeFeature(waveform: new byte[] { 9, 9 }, waveformVersion: "waveform-v5.6");

        _store.Upsert(feature, new[] { libraryA, libraryB });
        Assert.NotNull(_store.Get(feature.ItemId));

        _store.Delete(feature.ItemId);

        Assert.Null(_store.Get(feature.ItemId));
        Assert.Equal(0, CountLibraryJoinRows(feature.ItemId));
    }

    [Fact]
    public void Delete_IsANoOp_ForAnUnknownItem()
    {
        // The orphan sweep and the deletion flush both call this speculatively.
        _store.Delete(Guid.NewGuid());
    }

    [Fact]
    public void GetAllItemIds_ReturnsEveryStoredItem_Once()
    {
        var libraryId = Guid.NewGuid();
        var first = MakeFeature(waveform: new byte[] { 1 }, waveformVersion: "waveform-v5.6");
        var second = MakeFeature(waveform: new byte[] { 2 }, waveformVersion: "waveform-v5.6");

        _store.Upsert(first, new[] { libraryId, Guid.NewGuid() });
        _store.Upsert(second, new[] { libraryId });

        var ids = _store.GetAllItemIds();

        // Two library join rows for `first` must not surface it twice, or the sweep would
        // attempt the same delete repeatedly.
        Assert.Equal(2, ids.Count);
        Assert.Contains(first.ItemId, ids);
        Assert.Contains(second.ItemId, ids);
    }

    private int CountLibraryJoinRows(Guid itemId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "features.db")}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM TrackFeatureLibraries WHERE ItemId = $itemId";
        cmd.Parameters.AddWithValue("$itemId", itemId.ToString());
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void HasFreshWaveform_ReturnsFalse_WhenWaveformBlobIsNull()
    {
        // A row whose analyzer failed gets WaveformVersion set but Waveform left null;
        // freshness must reflect the blob presence, not just the version match.
        var feature = MakeFeature(
            waveform: null,
            waveformVersion: "waveform-v5.6");
        _store.Upsert(feature, Array.Empty<Guid>());

        Assert.False(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, feature.SourceMtime, -1));
    }

    [Fact]
    public void HasFreshWaveform_DistinguishesVersionAndMtime()
    {
        var feature = MakeFeature(
            waveform: new byte[] { 1 },
            waveformVersion: "waveform-v5.6",
            sourceMtime: 100,
            mixProfileVersion: MixProfileAnalyzer.Version);
        _store.Upsert(feature, Array.Empty<Guid>());

        Assert.True(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, -1));
        Assert.False(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.5", MixProfileAnalyzer.Version, 100, -1));
        Assert.False(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 101, -1));
    }

    [Fact]
    public void HasFreshWaveform_RequiresTheCurrentMixProfileVersion()
    {
        // A row analysed before the descriptor existed (or by an older descriptor) is stale,
        // so the next run re-decodes it once — however fresh its waveform.
        var none = MakeFeature(waveform: new byte[] { 1 }, waveformVersion: "waveform-v5.6", sourceMtime: 100);
        var older = MakeFeature(waveform: new byte[] { 1 }, waveformVersion: "waveform-v5.6", sourceMtime: 100, mixProfile: new byte[48], mixProfileVersion: "mixprofile-v0");
        var current = MakeFeature(waveform: new byte[] { 1 }, waveformVersion: "waveform-v5.6", sourceMtime: 100, mixProfile: new byte[48], mixProfileVersion: MixProfileAnalyzer.Version);
        foreach (var feature in new[] { none, older, current })
        {
            _store.Upsert(feature, Array.Empty<Guid>());
        }

        Assert.False(_store.HasFreshWaveform(none.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, -1));
        Assert.False(_store.HasFreshWaveform(older.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, -1));
        Assert.True(_store.HasFreshWaveform(current.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, -1));
        Assert.False(_store.HasFreshWaveform(current.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 101, -1));
    }

    [Fact]
    public void HasFreshWaveform_ANullDescriptorAtTheCurrentVersionIsFresh()
    {
        // A track too short or silent for a descriptor is analysed once, not every night.
        var feature = MakeFeature(waveform: new byte[] { 1 }, waveformVersion: "waveform-v5.6", sourceMtime: 100, mixProfileVersion: MixProfileAnalyzer.Version);
        _store.Upsert(feature, Array.Empty<Guid>());

        Assert.True(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, -1));
    }

    [Fact]
    public void Upsert_MixProfile_RoundtripsViaGetAndTheFeed()
    {
        var libraryId = Guid.NewGuid();
        var descriptor = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
        var feature = MakeFeature(waveform: new byte[] { 5 }, waveformVersion: "waveform-v5.6", mixProfile: descriptor, mixProfileVersion: MixProfileAnalyzer.Version);
        _store.Upsert(feature, new[] { libraryId });

        var loaded = _store.Get(feature.ItemId)!;
        Assert.Equal(descriptor, loaded.MixProfile);
        Assert.Equal(MixProfileAnalyzer.Version, loaded.MixProfileVersion);

        var row = Assert.Single(_store.GetProcessedSince(DateTimeOffset.MinValue, until: null, new[] { libraryId }, limit: 100, offset: 0));
        Assert.Equal(descriptor, row.MixProfile);
        Assert.Equal(MixProfileAnalyzer.Version, row.MixProfileVersion);
    }

    [Fact]
    public void Upsert_ADescriptorOntoAnExistingRow_ResurfacesItAfterAnIssuedCursor()
    {
        // A client that synced the waveform before the descriptor existed holds a cursor past
        // the row; writing the descriptor must move the row past that cursor again.
        var libraryId = Guid.NewGuid();
        var feature = MakeFeature(waveform: new byte[] { 5 }, waveformVersion: "waveform-v5.6");
        _store.Upsert(feature, new[] { libraryId });
        var cursor = SyncClock.IssueCursor();
        Assert.Empty(_store.GetProcessedSince(cursor, until: null, new[] { libraryId }, limit: 100, offset: 0));

        feature.MixProfile = new byte[48];
        feature.MixProfileVersion = MixProfileAnalyzer.Version;
        _store.Upsert(feature, new[] { libraryId });

        var row = Assert.Single(_store.GetProcessedSince(cursor, until: null, new[] { libraryId }, limit: 100, offset: 0));
        Assert.Equal(new byte[48], row.MixProfile);
        Assert.True(row.ProcessedAt > cursor);
    }

    [Fact]
    public void HasFreshWaveform_AFileReplacedWithItsModifiedDateKeptIsStale()
    {
        var feature = MakeFeature(waveform: new byte[] { 1 }, waveformVersion: "waveform-v5.6", sourceMtime: 100, mixProfileVersion: MixProfileAnalyzer.Version);
        feature.SourceSize = 5_000;
        _store.Upsert(feature, Array.Empty<Guid>());

        Assert.True(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, 5_000));
        Assert.False(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, 5_001));
        Assert.True(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, -1), "an unreadable size isn't compared");
    }

    [Fact]
    public void HasFreshWaveform_ARowWithNoSizeTakesTheFilesWithoutAReDecode()
    {
        // Rows from before sizes were stored: fresh on the mtime alone once, and from then on
        // the size is checked too.
        var feature = MakeFeature(waveform: new byte[] { 1 }, waveformVersion: "waveform-v5.6", sourceMtime: 100, mixProfileVersion: MixProfileAnalyzer.Version);
        _store.Upsert(feature, Array.Empty<Guid>());
        Assert.Null(_store.Get(feature.ItemId)!.SourceSize);

        Assert.True(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, 7_000));
        Assert.Equal(7_000, _store.Get(feature.ItemId)!.SourceSize);
        Assert.False(_store.HasFreshWaveform(feature.ItemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, 7_001));
    }

    [Fact]
    public void Upsert_AReAnalysisThatChangesNothingKeepsTheRowOffTheFeed()
    {
        // An analyser revision re-decodes every track; a row it leaves as it was must not be
        // re-sent to every device.
        var libraryId = Guid.NewGuid();
        var feature = MakeFeature(waveform: new byte[] { 5, 6 }, waveformVersion: "waveform-v5.6", sourceMtime: 100,
                                  mixProfile: new byte[48], mixProfileVersion: MixProfileAnalyzer.Version + "/2");
        _store.Upsert(feature, new[] { libraryId });
        var first = _store.Get(feature.ItemId)!;
        var cursor = SyncClock.IssueCursor();

        var again = Copy(feature);
        again.MixProfileVersion = MixProfileAnalyzer.Version + "/3";   // same wire version
        again.SourceMtime = 200;                                        // touched, same audio
        _store.Upsert(again, new[] { libraryId });

        Assert.Empty(_store.GetProcessedSince(cursor, until: null, new[] { libraryId }, limit: 100, offset: 0));
        var stored = _store.Get(feature.ItemId)!;
        Assert.Equal(first.ProcessedAt, stored.ProcessedAt);
        Assert.Equal(first.WaveformChangedAt, stored.WaveformChangedAt);
        Assert.Equal(MixProfileAnalyzer.Version + "/3", stored.MixProfileVersion);
        Assert.Equal(200, stored.SourceMtime);
    }

    [Fact]
    public void Upsert_ANewDescriptorResendsTheRowButNotItsWaveform()
    {
        var libraryId = Guid.NewGuid();
        var feature = MakeFeature(waveform: new byte[] { 5, 6 }, waveformVersion: "waveform-v5.6", mixProfile: new byte[48], mixProfileVersion: MixProfileAnalyzer.Version);
        _store.Upsert(feature, new[] { libraryId });
        var cursor = SyncClock.IssueCursor();

        var again = Copy(feature);
        again.MixProfile = Enumerable.Repeat((byte)1, 48).ToArray();
        _store.Upsert(again, new[] { libraryId });

        var row = Assert.Single(_store.GetProcessedSince(cursor, until: null, new[] { libraryId }, limit: 100, offset: 0));
        Assert.True(row.ProcessedAt > cursor);
        Assert.True(row.WaveformChangedAt <= cursor, "the client already holds this waveform");
    }

    [Theory]
    [InlineData("waveform")]
    [InlineData("version")]
    [InlineData("library")]
    public void Upsert_AChangedWaveformOrLibraryMovesTheWaveformsStamp(string change)
    {
        // A client of a library the track just joined never received its waveform.
        var libraryId = Guid.NewGuid();
        var feature = MakeFeature(waveform: new byte[] { 5, 6 }, waveformVersion: "waveform-v5.6", mixProfileVersion: MixProfileAnalyzer.Version);
        _store.Upsert(feature, new[] { libraryId });
        var cursor = SyncClock.IssueCursor();

        var again = Copy(feature);
        var libraries = new[] { libraryId };
        switch (change)
        {
            case "waveform": again.Waveform = new byte[] { 5, 7 }; break;
            case "version": again.WaveformVersion = "waveform-v5.7"; break;
            default: libraries = new[] { libraryId, Guid.NewGuid() }; break;
        }

        _store.Upsert(again, libraries);

        var row = Assert.Single(_store.GetProcessedSince(cursor, until: null, new[] { libraryId }, limit: 100, offset: 0));
        Assert.True(row.ProcessedAt > cursor);
        Assert.True(row.WaveformChangedAt > cursor);
    }

    [Fact]
    public void Open_ARowFromBeforeTheWaveformStampReadsItsLastStamp()
    {
        var feature = MakeFeature(waveform: new byte[] { 5 }, waveformVersion: "waveform-v5.6");
        _store.Upsert(feature, Array.Empty<Guid>());
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "features.db")}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE TrackFeatures SET WaveformChangedAt = NULL";
            cmd.ExecuteNonQuery();
        }

        var stored = _store.Get(feature.ItemId)!;
        Assert.Equal(stored.ProcessedAt, stored.WaveformChangedAt);
    }

    [Fact]
    public void Open_OnPreMixProfileSchema_AddsTheColumns_AndTheRowReadsStale()
    {
        var dir = Path.Combine(Path.GetTempPath(), "norisync-mixmigrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "features.db");
        var itemId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        try
        {
            using (var seed = new SqliteConnection($"Data Source={dbPath}"))
            {
                seed.Open();
                using var cmd = seed.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE TrackFeatures (
                        ItemId TEXT PRIMARY KEY,
                        Waveform BLOB,
                        WaveformVersion TEXT,
                        SourceMtime INTEGER NOT NULL,
                        ProcessedAt INTEGER NOT NULL
                    );
                    INSERT INTO TrackFeatures (ItemId, Waveform, WaveformVersion, SourceMtime, ProcessedAt)
                    VALUES ('22222222-2222-2222-2222-222222222222', x'4C540600', 'waveform-v5.6', 100, 0);";
                cmd.ExecuteNonQuery();
            }

            // Twice: the second open finds the columns already there.
            for (var open = 0; open < 2; open++)
            {
                using var store = new TrackFeatureStore(dir, NullLogger.Instance);
                var row = store.Get(itemId)!;
                Assert.Equal(new byte[] { 0x4C, 0x54, 0x06, 0x00 }, row.Waveform);
                Assert.Null(row.MixProfile);
                Assert.Null(row.MixProfileVersion);
                Assert.False(store.HasFreshWaveform(itemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, -1));
            }

            using (var store = new TrackFeatureStore(dir, NullLogger.Instance))
            {
                store.Upsert(
                    new TrackFeature
                    {
                        ItemId = itemId,
                        Waveform = new byte[] { 0x4C, 0x54, 0x06, 0x00 },
                        WaveformVersion = "waveform-v5.6",
                        MixProfile = new byte[48],
                        MixProfileVersion = MixProfileAnalyzer.Version,
                        SourceMtime = 100
                    },
                    Array.Empty<Guid>());
                Assert.True(store.HasFreshWaveform(itemId, "waveform-v5.6", MixProfileAnalyzer.Version, 100, -1));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void GetProcessedSince_ReturnsWaveformRows()
    {
        var libraryId = Guid.NewGuid();
        var feature = MakeFeature(
            waveform: new byte[] { 7, 7, 7 },
            waveformVersion: "waveform-v5.6");
        _store.Upsert(feature, new[] { libraryId });

        var rows = _store.GetProcessedSince(
            DateTimeOffset.MinValue,
            until: null,
            new[] { libraryId },
            limit: 100,
            offset: 0);

        var row = Assert.Single(rows);
        Assert.Equal(new byte[] { 7, 7, 7 }, row.Waveform);
        Assert.Equal("waveform-v5.6", row.WaveformVersion);
    }

    [Fact]
    public void GetProcessedSince_OrdersAscendingByCommitStamp()
    {
        // ProcessedAt is stamped as the row commits, whatever the caller set, so the feed orders
        // by commit and a caller's stale stamp can't land behind an issued cursor.
        var libraryId = Guid.NewGuid();
        var first = MakeFeature(waveform: new byte[] { 1 });
        first.ProcessedAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var second = MakeFeature(waveform: new byte[] { 2 });
        second.ProcessedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        _store.Upsert(first, new[] { libraryId });
        _store.Upsert(second, new[] { libraryId });

        var rows = _store.GetProcessedSince(
            DateTimeOffset.MinValue,
            until: null,
            new[] { libraryId },
            limit: 100,
            offset: 0);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].ProcessedAt < rows[1].ProcessedAt);
        Assert.Equal(first.ItemId, rows[0].ItemId);
        Assert.Equal(second.ItemId, rows[1].ItemId);
    }

    [Fact]
    public void Upsert_StampsAfterAnIssuedCursor()
    {
        var libraryId = Guid.NewGuid();
        var cursor = SyncClock.IssueCursor();

        var feature = MakeFeature(waveform: new byte[] { 3 });
        feature.ProcessedAt = cursor.AddMinutes(-10);
        _store.Upsert(feature, new[] { libraryId });

        var row = Assert.Single(_store.GetProcessedSince(cursor, until: null, new[] { libraryId }, limit: 100, offset: 0));
        Assert.Equal(feature.ItemId, row.ItemId);
    }

    [Fact]
    public void Open_OnOldEmbeddingSchema_DropsEmbeddingColumnsAndStillUpserts()
    {
        // A database written before embeddings were removed carries NOT NULL ModelVersion,
        // Embedding and EmbeddingDim columns plus an index over ModelVersion. Opening it must
        // drop those columns (otherwise the NOT NULL constraints break the new INSERT), then
        // serve reads and writes normally.
        var dir = Path.Combine(Path.GetTempPath(), "norisync-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "features.db");

        try
        {
            using (var seed = new SqliteConnection($"Data Source={dbPath}"))
            {
                seed.Open();
                using var cmd = seed.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE TrackFeatures (
                        ItemId TEXT PRIMARY KEY,
                        ModelVersion TEXT NOT NULL,
                        Embedding BLOB NOT NULL,
                        EmbeddingDim INTEGER NOT NULL,
                        Waveform BLOB,
                        WaveformVersion TEXT,
                        SourceMtime INTEGER NOT NULL,
                        ProcessedAt INTEGER NOT NULL
                    );
                    CREATE INDEX IX_TrackFeatures_ModelVersion ON TrackFeatures(ModelVersion);
                    INSERT INTO TrackFeatures (ItemId, ModelVersion, Embedding, EmbeddingDim, Waveform, WaveformVersion, SourceMtime, ProcessedAt)
                    VALUES ('11111111-1111-1111-1111-111111111111', 'old-model', x'00', 1, x'010203', 'waveform-v5.5', 100, 0);";
                cmd.ExecuteNonQuery();
            }

            var libraryId = Guid.NewGuid();
            using (var store = new TrackFeatureStore(dir, NullLogger.Instance))
            {
                // The legacy row's waveform survives the column drop.
                var legacy = store.Get(Guid.Parse("11111111-1111-1111-1111-111111111111"));
                Assert.NotNull(legacy);
                Assert.Equal(new byte[] { 1, 2, 3 }, legacy!.Waveform);

                // A fresh insert (which supplies none of the dropped columns) succeeds.
                var feature = MakeFeature(waveform: new byte[] { 9 }, waveformVersion: "waveform-v5.6");
                store.Upsert(feature, new[] { libraryId });
                Assert.Equal(new byte[] { 9 }, store.Get(feature.ItemId)!.Waveform);
            }

            // The embedding columns and their index are gone.
            using (var verify = new SqliteConnection($"Data Source={dbPath}"))
            {
                verify.Open();
                using var cols = verify.CreateCommand();
                cols.CommandText = "PRAGMA table_info(TrackFeatures)";
                var names = new System.Collections.Generic.List<string>();
                using (var reader = cols.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        names.Add(reader.GetString(1));
                    }
                }

                Assert.DoesNotContain("Embedding", names);
                Assert.DoesNotContain("EmbeddingDim", names);
                Assert.DoesNotContain("ModelVersion", names);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void Open_OnLegacyHalfRateWaveform_ConvertsItInPlaceToConvertedLegacyVersion()
    {
        // A row at the previous 60 Hz version carrying a v5 blob must be downsampled to the
        // current 30 Hz wire format on open, with ProcessedAt bumped so clients re-fetch a
        // valid-magic blob immediately — without the scheduled task having to re-decode the audio.
        // It is stamped ConvertedLegacyVersion (not the current analyzer Version) so it still reads
        // as stale and gets re-analyzed under the current mapping by the scheduled task.
        var dir = Path.Combine(Path.GetTempPath(), "norisync-wfmigrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var itemId = Guid.NewGuid();
        var oldProcessedAt = DateTimeOffset.UtcNow.AddDays(-30);
        // v5 header + two frames → converts to one merged v6 frame.
        var v5Blob = new byte[]
        {
            0x4C, 0x54, 0x05, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };

        try
        {
            using (var seed = new TrackFeatureStore(dir, NullLogger.Instance))
            {
                seed.Upsert(
                    new TrackFeature
                    {
                        ItemId = itemId,
                        Waveform = v5Blob,
                        WaveformVersion = WaveformAnalyzer.LegacyHalfRateVersion,
                        SourceMtime = 100,
                        ProcessedAt = oldProcessedAt
                    },
                    Array.Empty<Guid>());
            }

            using (var reopened = new TrackFeatureStore(dir, NullLogger.Instance))
            {
                var loaded = reopened.Get(itemId);
                Assert.NotNull(loaded);
                Assert.Equal(WaveformAnalyzer.Version, loaded!.WaveformVersion);
                Assert.NotNull(loaded.Waveform);
                Assert.Equal(WaveformAnalyzer.MagicHeader, loaded.Waveform![..4]);
                Assert.Equal(WaveformAnalyzer.MagicHeader.Length + 4, loaded.Waveform.Length);
                Assert.True(loaded.ProcessedAt > oldProcessedAt);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private static TrackFeature Copy(TrackFeature f) => new()
    {
        ItemId = f.ItemId,
        Waveform = f.Waveform,
        WaveformVersion = f.WaveformVersion,
        MixProfile = f.MixProfile,
        MixProfileVersion = f.MixProfileVersion,
        SourceMtime = f.SourceMtime,
        SourceSize = f.SourceSize
    };

    private static TrackFeature MakeFeature(
        byte[]? waveform = null,
        string? waveformVersion = null,
        long sourceMtime = 0,
        byte[]? mixProfile = null,
        string? mixProfileVersion = null)
    {
        return new TrackFeature
        {
            ItemId = Guid.NewGuid(),
            Waveform = waveform,
            WaveformVersion = waveformVersion,
            MixProfile = mixProfile,
            MixProfileVersion = mixProfileVersion,
            SourceMtime = sourceMtime == 0 ? DateTimeOffset.UtcNow.UtcTicks : sourceMtime,
            ProcessedAt = DateTimeOffset.UtcNow
        };
    }
}
