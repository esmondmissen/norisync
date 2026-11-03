using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Round-trip and lifecycle tests for <see cref="AnimatedArtworkStore"/>: a committed asset must
/// survive a read with its file on disk, replacing it must overwrite in place, the Primary tag map
/// must reflect the stored SHA, and deletes (single slot and whole item) must remove both the row
/// and the backing file.
/// </summary>
public sealed class AnimatedArtworkStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AnimatedArtworkStore _store;

    public AnimatedArtworkStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-artwork-tests-" + Guid.NewGuid().ToString("N"));
        _store = new AnimatedArtworkStore(_tempDir, NullLogger.Instance);
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
            // Leave the temp dir on file-lock contention; nothing important leaks.
        }
    }

    [Fact]
    public void Commit_Roundtrips_ViaGet_AndWritesFile()
    {
        var itemId = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var stored = Commit(itemId, "Primary", "video/mp4", bytes, sha: "abc123", durationSeconds: 6.5, width: 1000, height: 1000, moovAtFront: true);

        Assert.True(File.Exists(stored.FilePath));
        Assert.Equal(bytes, File.ReadAllBytes(stored.FilePath));

        var loaded = _store.Get(itemId, "Primary");
        Assert.NotNull(loaded);
        Assert.Equal("video/mp4", loaded!.ContentType);
        Assert.Equal("abc123", loaded.Sha256);
        Assert.Equal(6.5, loaded.DurationSeconds);
        Assert.Equal(1000, loaded.Width);
        Assert.Equal(1000, loaded.Height);
        Assert.True(loaded.MoovAtFront);
        Assert.Equal(bytes.Length, loaded.ByteLength);
    }

    [Fact]
    public void Commit_Replacing_SameSlot_OverwritesInPlace()
    {
        var itemId = Guid.NewGuid();
        Commit(itemId, "Primary", "video/mp4", new byte[] { 1, 1, 1 }, sha: "first");
        var second = Commit(itemId, "Primary", "video/mp4", new byte[] { 2, 2, 2, 2 }, sha: "second");

        var loaded = _store.Get(itemId, "Primary");
        Assert.NotNull(loaded);
        Assert.Equal("second", loaded!.Sha256);
        Assert.Equal(new byte[] { 2, 2, 2, 2 }, File.ReadAllBytes(second.FilePath));
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void Commit_Replacing_WithDifferentExtension_RemovesOldFile()
    {
        var itemId = Guid.NewGuid();
        var first = Commit(itemId, "Primary", "video/mp4", new byte[] { 1 }, sha: "mp4");
        Assert.EndsWith(".mp4", first.FilePath, StringComparison.Ordinal);

        var second = Commit(itemId, "Primary", "video/webm", new byte[] { 2 }, sha: "webm");
        Assert.EndsWith(".webm", second.FilePath, StringComparison.Ordinal);

        Assert.False(File.Exists(first.FilePath));
        Assert.True(File.Exists(second.FilePath));
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void GetTags_ReturnsShaMap_ForImageType()
    {
        var albumA = Guid.NewGuid();
        var albumB = Guid.NewGuid();
        Commit(albumA, "Primary", "video/mp4", new byte[] { 1 }, sha: "shaA");
        Commit(albumB, "Primary", "video/mp4", new byte[] { 2 }, sha: "shaB");
        Commit(albumA, "Backdrop", "video/mp4", new byte[] { 3 }, sha: "backdropA");

        var primary = _store.GetTags("Primary");
        Assert.Equal(2, primary.Count);
        Assert.Equal("shaA", primary[albumA]);
        Assert.Equal("shaB", primary[albumB]);
        Assert.DoesNotContain(albumB, _store.GetTags("Backdrop").Keys);
    }

    [Fact]
    public void Delete_RemovesRowAndFile()
    {
        var itemId = Guid.NewGuid();
        var stored = Commit(itemId, "Primary", "video/mp4", new byte[] { 1 }, sha: "x");

        Assert.True(_store.Delete(itemId, "Primary"));
        Assert.Null(_store.Get(itemId, "Primary"));
        Assert.False(File.Exists(stored.FilePath));
        Assert.False(_store.Delete(itemId, "Primary"));
    }

    [Fact]
    public void DeleteItem_RemovesAllSlotsAndFiles()
    {
        var itemId = Guid.NewGuid();
        var a = Commit(itemId, "Primary", "video/mp4", new byte[] { 1 }, sha: "p");
        var b = Commit(itemId, "Backdrop", "video/mp4", new byte[] { 2 }, sha: "b");

        var removed = _store.DeleteItem(itemId);
        Assert.Equal(2, removed);
        Assert.Empty(_store.GetForItem(itemId));
        Assert.False(File.Exists(a.FilePath));
        Assert.False(File.Exists(b.FilePath));
    }

    [Fact]
    public void ReclassifyTallAlbumArtwork_MovesPortraitPrimary_IntoTallSlot()
    {
        var itemId = Guid.NewGuid();
        var bytes = new byte[] { 9, 8, 7 };
        var original = Commit(itemId, "Primary", "video/mp4", bytes, sha: "portrait", width: 1080, height: 1920);

        Assert.Equal(1, _store.ReclassifyTallAlbumArtwork());

        Assert.Null(_store.Get(itemId, "Primary"));
        var moved = _store.Get(itemId, "Tall");
        Assert.NotNull(moved);
        Assert.Equal("portrait", moved!.Sha256);
        Assert.Equal(1080, moved.Width);
        Assert.Equal(1920, moved.Height);
        Assert.EndsWith("_Tall.mp4", moved.FileName, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(moved.FilePath));
        Assert.False(File.Exists(original.FilePath));
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void ReclassifyTallAlbumArtwork_MovesPortraitBackdrop_IntoTallSlot()
    {
        var itemId = Guid.NewGuid();
        Commit(itemId, "Backdrop", "video/webm", new byte[] { 1 }, sha: "apple-tall", width: 1000, height: 1334);

        Assert.Equal(1, _store.ReclassifyTallAlbumArtwork());

        Assert.Null(_store.Get(itemId, "Backdrop"));
        var moved = _store.Get(itemId, "Tall");
        Assert.NotNull(moved);
        Assert.EndsWith("_Tall.webm", moved!.FileName, StringComparison.Ordinal);
        Assert.True(File.Exists(moved.FilePath));
    }

    [Theory]
    [InlineData(1000, 1000)]   // square
    [InlineData(1000, 1100)]   // within the square tolerance
    [InlineData(1920, 1080)]   // wide
    [InlineData(null, null)]   // dimensions never probed
    public void ReclassifyTallAlbumArtwork_LeavesEverythingElseInPlace(int? width, int? height)
    {
        var itemId = Guid.NewGuid();
        Commit(itemId, "Primary", "video/mp4", new byte[] { 1 }, sha: "keep", width: width, height: height);

        Assert.Equal(0, _store.ReclassifyTallAlbumArtwork());

        Assert.NotNull(_store.Get(itemId, "Primary"));
        Assert.Null(_store.Get(itemId, "Tall"));
    }

    [Fact]
    public void ReclassifyTallAlbumArtwork_LeavesArtistArtworkAlone()
    {
        var itemId = Guid.NewGuid();
        Commit(itemId, "Primary", "video/mp4", new byte[] { 1 }, sha: "artist", width: 1080, height: 1920, itemType: "Artist");

        Assert.Equal(0, _store.ReclassifyTallAlbumArtwork());

        Assert.NotNull(_store.Get(itemId, "Primary"));
    }

    [Fact]
    public void ReclassifyTallAlbumArtwork_SkipsAlbumsThatAlreadyHaveATallAsset()
    {
        var itemId = Guid.NewGuid();
        Commit(itemId, "Primary", "video/mp4", new byte[] { 1 }, sha: "primary", width: 1080, height: 1920);
        Commit(itemId, "Tall", "video/mp4", new byte[] { 2 }, sha: "existing-tall", width: 1080, height: 1920);

        Assert.Equal(0, _store.ReclassifyTallAlbumArtwork());

        Assert.Equal("primary", _store.Get(itemId, "Primary")?.Sha256);
        Assert.Equal("existing-tall", _store.Get(itemId, "Tall")?.Sha256);
    }

    [Fact]
    public void Open_MigratesLegacyDatabaseOnce_ThenLeavesDeliberateUploadsAlone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "norisync-artwork-tests-" + Guid.NewGuid().ToString("N"));
        var legacy = Guid.NewGuid();
        var deliberate = Guid.NewGuid();

        try
        {
            using (var store = new AnimatedArtworkStore(dir, NullLogger.Instance))
            {
                CommitTo(store, legacy, "Primary", "video/mp4", new byte[] { 1 }, sha: "legacy", width: 1080, height: 1920);
            }

            // Pretend the row predates the tall slot.
            SetUserVersion(dir, 0);

            using (var store = new AnimatedArtworkStore(dir, NullLogger.Instance))
            {
                Assert.Null(store.Get(legacy, "Primary"));
                Assert.Equal("legacy", store.Get(legacy, "Tall")?.Sha256);

                // A portrait loop uploaded into the square slot after the migration is a choice,
                // not a leftover — reopening must not move it.
                CommitTo(store, deliberate, "Primary", "video/mp4", new byte[] { 2 }, sha: "chosen", width: 1080, height: 1920);
            }

            using (var store = new AnimatedArtworkStore(dir, NullLogger.Instance))
            {
                Assert.Equal("chosen", store.Get(deliberate, "Primary")?.Sha256);
                Assert.Null(store.Get(deliberate, "Tall"));
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
                // Leave the temp dir on file-lock contention; nothing important leaks.
            }
        }
    }

    /// <summary>
    /// A ladder step used to name the long edge and now names the short one, so renditions cut under
    /// the old reading are filed under a number that no longer describes their pixels. The v2
    /// migration must discard every one of them — rows and files, including a file no row points at —
    /// while leaving masters untouched, and must not fire again on a later open.
    /// </summary>
    [Fact]
    public void Open_SchemaV2_DiscardsRenditionsCutUnderLongEdgeSizing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "norisync-artwork-tests-" + Guid.NewGuid().ToString("N"));
        var itemId = Guid.NewGuid();
        string derivedPath;
        string orphanPath;

        try
        {
            using (var store = new AnimatedArtworkStore(dir, NullLogger.Instance))
            {
                CommitTo(store, itemId, "Tall", "video/mp4", new byte[] { 1 }, sha: "master", width: 1200, height: 1600);

                var temp = store.NewDerivativeTempPath();
                File.WriteAllBytes(temp, new byte[] { 9 });
                derivedPath = store.CommitDerivative(
                    new AnimatedArtworkDerivative
                    {
                        ItemId = itemId,
                        ImageType = "Tall",
                        ShortEdge = 720,
                        ContentType = "video/mp4",
                        ByteLength = 1,
                        Sha256 = "derived",
                        SourceSha256 = "master",
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    temp).FilePath;

                // A commit that died between moving the file and writing the row leaves this behind;
                // sweeping the directory is what clears it.
                orphanPath = Path.Combine(Path.GetDirectoryName(derivedPath)!, "orphan_Tall_2160.mp4");
                File.WriteAllBytes(orphanPath, new byte[] { 5 });
            }

            SetUserVersion(dir, 1);

            using (var store = new AnimatedArtworkStore(dir, NullLogger.Instance))
            {
                Assert.Null(store.GetDerivative(itemId, "Tall", 720));
                Assert.False(File.Exists(derivedPath));
                Assert.False(File.Exists(orphanPath));

                // The master is not a cache and must survive untouched.
                Assert.Equal("master", store.Get(itemId, "Tall")?.Sha256);

                var temp = store.NewDerivativeTempPath();
                File.WriteAllBytes(temp, new byte[] { 4 });
                store.CommitDerivative(
                    new AnimatedArtworkDerivative
                    {
                        ItemId = itemId,
                        ImageType = "Tall",
                        ShortEdge = 720,
                        ContentType = "video/mp4",
                        ByteLength = 1,
                        Sha256 = "fresh",
                        SourceSha256 = "master",
                        CreatedAt = DateTimeOffset.UtcNow
                    },
                    temp);
            }

            // Runs once: a rendition cut after the migration is still there on the next open.
            using (var store = new AnimatedArtworkStore(dir, NullLogger.Instance))
            {
                Assert.Equal("fresh", store.GetDerivative(itemId, "Tall", 720)?.Sha256);
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
                // Leave the temp dir on file-lock contention; nothing important leaks.
            }
        }
    }

    [Fact]
    public void CommitDerivative_Roundtrips_AndKeepsItsOwnTag()
    {
        var itemId = Guid.NewGuid();
        var master = Commit(itemId, "Tall", "video/mp4", new byte[] { 1, 2, 3 }, sha: "master", width: 1200, height: 1600);
        var bytes = new byte[] { 7, 7 };

        var stored = CommitDerivative(itemId, "Tall", 720, bytes, sha: "derived720", sourceSha: master.Sha256, width: 540, height: 720);

        Assert.True(File.Exists(stored.FilePath));
        Assert.Contains("derived", stored.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("_Tall_720.mp4", stored.FileName, StringComparison.Ordinal);

        var loaded = _store.GetDerivative(itemId, "Tall", 720);
        Assert.NotNull(loaded);
        Assert.Equal("derived720", loaded!.Sha256);
        Assert.Equal("master", loaded.SourceSha256);
        Assert.Equal(540, loaded.Width);
        Assert.Equal(720, loaded.Height);
        Assert.Equal(bytes, File.ReadAllBytes(loaded.FilePath));

        // The master's own tag is untouched, so a client holding either size revalidates independently.
        Assert.Equal("master", _store.Get(itemId, "Tall")?.Sha256);
        Assert.Null(_store.GetDerivative(itemId, "Tall", 1080));
    }

    [Fact]
    public void Commit_ReplacingMaster_DropsItsCachedSizes()
    {
        var itemId = Guid.NewGuid();
        Commit(itemId, "Tall", "video/mp4", new byte[] { 1 }, sha: "first", width: 1200, height: 1600);
        var derived = CommitDerivative(itemId, "Tall", 720, new byte[] { 9 }, sha: "d", sourceSha: "first");
        Assert.True(File.Exists(derived.FilePath));

        Commit(itemId, "Tall", "video/mp4", new byte[] { 2 }, sha: "second", width: 1200, height: 1600);

        Assert.Null(_store.GetDerivative(itemId, "Tall", 720));
        Assert.False(File.Exists(derived.FilePath));
    }

    [Fact]
    public void Commit_ToOneSlot_LeavesAnotherSlotsCachedSizes()
    {
        var itemId = Guid.NewGuid();
        Commit(itemId, "Primary", "video/mp4", new byte[] { 1 }, sha: "sq", width: 1080, height: 1080);
        Commit(itemId, "Tall", "video/mp4", new byte[] { 2 }, sha: "tall", width: 1200, height: 1600);
        CommitDerivative(itemId, "Primary", 720, new byte[] { 8 }, sha: "sq720", sourceSha: "sq");
        var tallDerived = CommitDerivative(itemId, "Tall", 720, new byte[] { 9 }, sha: "tall720", sourceSha: "tall");

        Commit(itemId, "Primary", "video/mp4", new byte[] { 3 }, sha: "sq2", width: 1080, height: 1080);

        Assert.Null(_store.GetDerivative(itemId, "Primary", 720));
        Assert.NotNull(_store.GetDerivative(itemId, "Tall", 720));
        Assert.True(File.Exists(tallDerived.FilePath));
    }

    [Fact]
    public void Delete_And_DeleteItem_DropCachedSizes()
    {
        var itemId = Guid.NewGuid();
        Commit(itemId, "Primary", "video/mp4", new byte[] { 1 }, sha: "sq", width: 1080, height: 1080);
        var slotDerived = CommitDerivative(itemId, "Primary", 480, new byte[] { 8 }, sha: "sq480", sourceSha: "sq");

        Assert.True(_store.Delete(itemId, "Primary"));
        Assert.Null(_store.GetDerivative(itemId, "Primary", 480));
        Assert.False(File.Exists(slotDerived.FilePath));

        Commit(itemId, "Tall", "video/mp4", new byte[] { 2 }, sha: "tall", width: 1200, height: 1600);
        var itemDerived = CommitDerivative(itemId, "Tall", 720, new byte[] { 9 }, sha: "tall720", sourceSha: "tall");

        _store.DeleteItem(itemId);
        Assert.Null(_store.GetDerivative(itemId, "Tall", 720));
        Assert.False(File.Exists(itemDerived.FilePath));
    }

    private AnimatedArtworkDerivative CommitDerivative(
        Guid itemId,
        string imageType,
        int shortEdge,
        byte[] bytes,
        string sha,
        string sourceSha,
        int? width = null,
        int? height = null)
    {
        var temp = _store.NewDerivativeTempPath();
        File.WriteAllBytes(temp, bytes);

        return _store.CommitDerivative(
            new AnimatedArtworkDerivative
            {
                ItemId = itemId,
                ImageType = imageType,
                ShortEdge = shortEdge,
                ContentType = "video/mp4",
                ByteLength = bytes.Length,
                Width = width,
                Height = height,
                Sha256 = sha,
                SourceSha256 = sourceSha,
                CreatedAt = DateTimeOffset.UtcNow
            },
            temp);
    }

    private static void SetUserVersion(string dataPath, int version)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dataPath, "animatedartwork.db")}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture);
        cmd.ExecuteNonQuery();
    }

    private static AnimatedArtworkRecord CommitTo(
        AnimatedArtworkStore store,
        Guid itemId,
        string imageType,
        string contentType,
        byte[] bytes,
        string sha,
        double? durationSeconds = null,
        int? width = null,
        int? height = null,
        bool? moovAtFront = null,
        string itemType = "Album")
    {
        var temp = store.NewTempPath();
        File.WriteAllBytes(temp, bytes);

        var record = new AnimatedArtworkRecord
        {
            ItemId = itemId,
            ItemType = itemType,
            ImageType = imageType,
            ContentType = contentType,
            ByteLength = bytes.Length,
            DurationSeconds = durationSeconds,
            Width = width,
            Height = height,
            MoovAtFront = moovAtFront,
            Sha256 = sha,
            UploadedAt = DateTimeOffset.UtcNow,
            UploadedBy = null
        };

        return store.Commit(record, temp);
    }

    private AnimatedArtworkRecord Commit(
        Guid itemId,
        string imageType,
        string contentType,
        byte[] bytes,
        string sha,
        double? durationSeconds = null,
        int? width = null,
        int? height = null,
        bool? moovAtFront = null,
        string itemType = "Album")
        => CommitTo(_store, itemId, imageType, contentType, bytes, sha, durationSeconds, width, height, moovAtFront, itemType);
}
