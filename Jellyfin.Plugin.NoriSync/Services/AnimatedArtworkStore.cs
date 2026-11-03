using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Persists animated-artwork assets: the binaries as files under <c>{dataPath}/artwork</c> and a
/// SQLite index row per (item, image type) carrying their metadata. Blobs are kept on disk rather
/// than in SQLite so the serve endpoint can stream them with HTTP range support and never has to
/// materialize a multi-megabyte video in memory.
/// </summary>
public sealed class AnimatedArtworkStore : IDisposable
{
    // Data revisions applied on open, tracked in the database's PRAGMA user_version. Bump both
    // constants together when adding a migration to ApplyMigrations; the SQL is a literal because
    // PRAGMA values cannot be bound as parameters and CommandText must stay constant.
    private const int CurrentSchemaVersion = 2;
    private const string SetSchemaVersionSql = "PRAGMA user_version = 2";

    // Held apart from the rest of the DDL because the v2 migration rebuilds this table: both places
    // need the identical statement, and both need it as a compile-time constant.
    private const string CreateDerivativeTableSql = @"
        CREATE TABLE IF NOT EXISTS AnimatedArtworkDerivative (
            ItemId TEXT NOT NULL,
            ImageType TEXT NOT NULL,
            ShortEdge INTEGER NOT NULL,
            ContentType TEXT NOT NULL,
            FileName TEXT NOT NULL,
            ByteLength INTEGER NOT NULL,
            DurationSeconds REAL,
            Width INTEGER,
            Height INTEGER,
            Sha256 TEXT NOT NULL,
            SourceSha256 TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            PRIMARY KEY (ItemId, ImageType, ShortEdge)
        );";

    private const string DropDerivativeTableSql = "DROP TABLE IF EXISTS AnimatedArtworkDerivative";

    // Slots that held album loops before the tall slot existed, and so may contain a portrait
    // asset that belongs in it. Ordered: a portrait loop in the square slot is the unambiguous
    // case, so it claims the tall slot first if an album somehow has both.
    private static readonly string[] LegacyAlbumSlots = { ArtworkShape.SquareImageType, "Backdrop" };

    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly object _connectionLock = new();
    private readonly string _artworkDir;
    private readonly string _tempDir;
    private readonly string _derivedDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimatedArtworkStore"/> class.
    /// </summary>
    /// <param name="dataPath">The plugin data directory. The index db and an <c>artwork</c> subfolder are created here.</param>
    /// <param name="logger">Logger instance.</param>
    public AnimatedArtworkStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        _artworkDir = Path.Combine(dataPath, "artwork");
        _tempDir = Path.Combine(_artworkDir, "tmp");
        _derivedDir = Path.Combine(_artworkDir, "derived");
        Directory.CreateDirectory(_artworkDir);
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_derivedDir);

        _logger = logger;

        var dbPath = Path.Combine(dataPath, "animatedartwork.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        ApplyMigrations();
        _logger.LogInformation("Animated artwork database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Returns an absolute path inside the store's temp directory for staging an upload before it is
    /// committed. Callers write the request body here, probe it, then hand it to <see cref="Commit"/>.
    /// The temp directory is on the same volume as the final files so the commit is an atomic move.
    /// </summary>
    /// <returns>A unique temp file path.</returns>
    public string NewTempPath()
        => Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".tmp");

    /// <summary>
    /// Commits a staged upload: moves <paramref name="sourceTempPath"/> into the artwork directory and
    /// upserts the index row. Any previously stored asset for the same (item, image type) is replaced,
    /// and its backing file is removed if it had a different name.
    /// </summary>
    /// <param name="record">The record to persist. <see cref="AnimatedArtworkRecord.FileName"/> and
    /// <see cref="AnimatedArtworkRecord.FilePath"/> are assigned by this method.</param>
    /// <param name="sourceTempPath">The staged temp file (from <see cref="NewTempPath"/>) holding the asset bytes.</param>
    /// <returns>The persisted record, with file name and absolute path populated.</returns>
    public AnimatedArtworkRecord Commit(AnimatedArtworkRecord record, string sourceTempPath)
    {
        ArgumentNullException.ThrowIfNull(record);

        var extension = ExtensionFor(record.ContentType);
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{record.ItemId:N}_{record.ImageType}{extension}");
        var finalPath = Path.Combine(_artworkDir, fileName);

        lock (_connectionLock)
        {
            var previousFile = GetFileNameNoLock(record.ItemId, record.ImageType);

            File.Move(sourceTempPath, finalPath, overwrite: true);

            if (previousFile != null
                && !string.Equals(previousFile, fileName, StringComparison.Ordinal))
            {
                TryDeleteFile(Path.Combine(_artworkDir, previousFile));
            }

            record.FileName = fileName;
            record.FilePath = finalPath;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO AnimatedArtwork
                    (ItemId, ItemType, ImageType, ContentType, FileName, ByteLength,
                     DurationSeconds, Width, Height, MoovAtFront, Sha256, UploadedAt, UploadedBy)
                VALUES
                    ($itemId, $itemType, $imageType, $contentType, $fileName, $byteLength,
                     $durationSeconds, $width, $height, $moovAtFront, $sha256, $uploadedAt, $uploadedBy)
                ON CONFLICT(ItemId, ImageType) DO UPDATE SET
                    ItemType = excluded.ItemType,
                    ContentType = excluded.ContentType,
                    FileName = excluded.FileName,
                    ByteLength = excluded.ByteLength,
                    DurationSeconds = excluded.DurationSeconds,
                    Width = excluded.Width,
                    Height = excluded.Height,
                    MoovAtFront = excluded.MoovAtFront,
                    Sha256 = excluded.Sha256,
                    UploadedAt = excluded.UploadedAt,
                    UploadedBy = excluded.UploadedBy";

            cmd.Parameters.AddWithValue("$itemId", record.ItemId.ToString("N"));
            cmd.Parameters.AddWithValue("$itemType", record.ItemType);
            cmd.Parameters.AddWithValue("$imageType", record.ImageType);
            cmd.Parameters.AddWithValue("$contentType", record.ContentType);
            cmd.Parameters.AddWithValue("$fileName", fileName);
            cmd.Parameters.AddWithValue("$byteLength", record.ByteLength);
            cmd.Parameters.AddWithValue("$durationSeconds", (object?)record.DurationSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$width", (object?)record.Width ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$height", (object?)record.Height ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$moovAtFront", record.MoovAtFront == null ? DBNull.Value : record.MoovAtFront.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$sha256", record.Sha256);
            cmd.Parameters.AddWithValue("$uploadedAt", record.UploadedAt.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$uploadedBy", (object?)record.UploadedBy ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            // Every cached size of the slot describes the master that just got replaced.
            DeleteDerivativesNoLock(record.ItemId, record.ImageType);
        }

        return record;
    }

    /// <summary>
    /// Gets the stored artwork record for an item's image slot, or <see langword="null"/> when none
    /// exists. The returned record has its absolute <see cref="AnimatedArtworkRecord.FilePath"/> set.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="imageType">The image slot, e.g. <c>"Primary"</c>.</param>
    /// <returns>The record, or <see langword="null"/>.</returns>
    public AnimatedArtworkRecord? Get(Guid itemId, string imageType)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT ItemId, ItemType, ImageType, ContentType, FileName, ByteLength,
                       DurationSeconds, Width, Height, MoovAtFront, Sha256, UploadedAt, UploadedBy
                FROM AnimatedArtwork
                WHERE ItemId = $itemId AND ImageType = $imageType";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            cmd.Parameters.AddWithValue("$imageType", imageType);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }
    }

    /// <summary>
    /// Gets every stored artwork record for an item, across all image slots.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <returns>The records (possibly empty), each with its absolute path populated.</returns>
    public IReadOnlyList<AnimatedArtworkRecord> GetForItem(Guid itemId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT ItemId, ItemType, ImageType, ContentType, FileName, ByteLength,
                       DurationSeconds, Width, Height, MoovAtFront, Sha256, UploadedAt, UploadedBy
                FROM AnimatedArtwork
                WHERE ItemId = $itemId
                ORDER BY ImageType";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString("N"));

            var results = new List<AnimatedArtworkRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadRecord(reader));
            }

            return results;
        }
    }

    /// <summary>
    /// Returns a map of item id to cache tag (SHA-256) for every asset of the given image type. Used
    /// to stamp the sync DTOs with a non-null tag exactly for the items that have animated artwork.
    /// The animated set is curated and sparse, so loading the whole map per request is cheap and
    /// avoids an N+1 lookup while paging albums/artists.
    /// </summary>
    /// <param name="imageType">The image slot, e.g. <c>"Primary"</c>.</param>
    /// <returns>Item id to SHA-256 tag.</returns>
    public IReadOnlyDictionary<Guid, string> GetTags(string imageType)
    {
        var map = new Dictionary<Guid, string>();
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT ItemId, Sha256 FROM AnimatedArtwork WHERE ImageType = $imageType";
            cmd.Parameters.AddWithValue("$imageType", imageType);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                {
                    map[id] = reader.GetString(1);
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Deletes the artwork for one image slot of an item, removing both the index row and the backing
    /// file.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="imageType">The image slot.</param>
    /// <returns><see langword="true"/> if a record existed and was removed.</returns>
    public bool Delete(Guid itemId, string imageType)
    {
        lock (_connectionLock)
        {
            var fileName = GetFileNameNoLock(itemId, imageType);
            if (fileName == null)
            {
                return false;
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM AnimatedArtwork WHERE ItemId = $itemId AND ImageType = $imageType";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            cmd.Parameters.AddWithValue("$imageType", imageType);
            cmd.ExecuteNonQuery();

            DeleteDerivativesNoLock(itemId, imageType);
            TryDeleteFile(Path.Combine(_artworkDir, fileName));
            return true;
        }
    }

    /// <summary>
    /// Deletes every artwork asset for an item (all image slots). Used to clean up when the underlying
    /// library item is removed.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <returns>The number of assets removed.</returns>
    public int DeleteItem(Guid itemId)
    {
        lock (_connectionLock)
        {
            var fileNames = new List<string>();
            using (var select = _connection.CreateCommand())
            {
                select.CommandText = "SELECT FileName FROM AnimatedArtwork WHERE ItemId = $itemId";
                select.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    fileNames.Add(reader.GetString(0));
                }
            }

            if (fileNames.Count == 0)
            {
                return 0;
            }

            using (var delete = _connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM AnimatedArtwork WHERE ItemId = $itemId";
                delete.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
                delete.ExecuteNonQuery();
            }

            using (var deleteAttempts = _connection.CreateCommand())
            {
                deleteAttempts.CommandText = "DELETE FROM AppleArtworkAttempt WHERE ItemId = $itemId";
                deleteAttempts.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
                deleteAttempts.ExecuteNonQuery();
            }

            DeleteDerivativesNoLock(itemId, imageType: null);

            foreach (var fileName in fileNames)
            {
                TryDeleteFile(Path.Combine(_artworkDir, fileName));
            }

            return fileNames.Count;
        }
    }

    /// <summary>
    /// Records that the Apple animated-artwork backfill attempted a given (item, image type),
    /// stamping the current time and a status (<c>"found"</c>, <c>"nomatch"</c>, or <c>"error"</c>).
    /// Upserts one row per slot. Used by <see cref="HasRecentAppleAttempt"/> as a negative cache so
    /// the task doesn't re-query the service for the many albums Apple has no motion art for.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="imageType">The image slot.</param>
    /// <param name="status">The attempt outcome.</param>
    public void RecordAppleAttempt(Guid itemId, string imageType, string status)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO AppleArtworkAttempt (ItemId, ImageType, Status, AttemptedAtTicks)
                VALUES ($itemId, $imageType, $status, $ticks)
                ON CONFLICT(ItemId, ImageType) DO UPDATE SET
                    Status = excluded.Status,
                    AttemptedAtTicks = excluded.AttemptedAtTicks";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            cmd.Parameters.AddWithValue("$imageType", imageType);
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$ticks", DateTimeOffset.UtcNow.UtcTicks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns whether the Apple backfill attempted a given (item, image type) within the retry
    /// window, regardless of outcome. Lets the task skip slots it recently tried (a miss stays a miss
    /// until Apple publishes motion art, so re-checking every run is wasteful) while still re-trying
    /// once the window elapses.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="imageType">The image slot.</param>
    /// <param name="window">How long an attempt suppresses re-tries.</param>
    /// <returns><see langword="true"/> when a recent attempt exists.</returns>
    public bool HasRecentAppleAttempt(Guid itemId, string imageType, TimeSpan window)
    {
        var threshold = DateTimeOffset.UtcNow.Subtract(window).UtcTicks;
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT AttemptedAtTicks FROM AppleArtworkAttempt WHERE ItemId = $itemId AND ImageType = $imageType";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            cmd.Parameters.AddWithValue("$imageType", imageType);
            var result = cmd.ExecuteScalar();
            if (result == null || result is DBNull)
            {
                return false;
            }

            return Convert.ToInt64(result, CultureInfo.InvariantCulture) >= threshold;
        }
    }

    /// <summary>
    /// Returns an absolute path for staging a derivative before it is committed. Shares the temp
    /// directory with uploads so the commit stays a same-volume move.
    /// </summary>
    /// <returns>A unique temp file path.</returns>
    public string NewDerivativeTempPath() => NewTempPath();

    /// <summary>
    /// Gets a cached rendition of a master, or <see langword="null"/> when that size has not been
    /// produced yet. The caller is responsible for checking <see cref="AnimatedArtworkDerivative.SourceSha256"/>
    /// against the master it holds, since a replaced master leaves stale rows behind if a delete
    /// ever fails.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="imageType">The master's image slot.</param>
    /// <param name="shortEdge">The ladder step.</param>
    /// <returns>The derivative, or <see langword="null"/>.</returns>
    public AnimatedArtworkDerivative? GetDerivative(Guid itemId, string imageType, int shortEdge)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT ItemId, ImageType, ShortEdge, ContentType, FileName, ByteLength,
                       DurationSeconds, Width, Height, Sha256, SourceSha256, CreatedAt
                FROM AnimatedArtworkDerivative
                WHERE ItemId = $itemId AND ImageType = $imageType AND ShortEdge = $shortEdge";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            cmd.Parameters.AddWithValue("$imageType", imageType);
            cmd.Parameters.AddWithValue("$shortEdge", shortEdge);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDerivative(reader) : null;
        }
    }

    /// <summary>
    /// Commits a staged rendition: moves it into the derivative directory and upserts its row,
    /// replacing any rendition already held for the same (item, slot, size).
    /// </summary>
    /// <param name="derivative">The rendition to persist. Its file name and path are assigned here.</param>
    /// <param name="sourceTempPath">The staged temp file holding the rendition bytes.</param>
    /// <returns>The persisted rendition.</returns>
    public AnimatedArtworkDerivative CommitDerivative(AnimatedArtworkDerivative derivative, string sourceTempPath)
    {
        ArgumentNullException.ThrowIfNull(derivative);

        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{derivative.ItemId:N}_{derivative.ImageType}_{derivative.ShortEdge}{ExtensionFor(derivative.ContentType)}");
        var finalPath = Path.Combine(_derivedDir, fileName);

        lock (_connectionLock)
        {
            File.Move(sourceTempPath, finalPath, overwrite: true);

            derivative.FileName = fileName;
            derivative.FilePath = finalPath;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO AnimatedArtworkDerivative
                    (ItemId, ImageType, ShortEdge, ContentType, FileName, ByteLength,
                     DurationSeconds, Width, Height, Sha256, SourceSha256, CreatedAt)
                VALUES
                    ($itemId, $imageType, $shortEdge, $contentType, $fileName, $byteLength,
                     $durationSeconds, $width, $height, $sha256, $sourceSha256, $createdAt)
                ON CONFLICT(ItemId, ImageType, ShortEdge) DO UPDATE SET
                    ContentType = excluded.ContentType,
                    FileName = excluded.FileName,
                    ByteLength = excluded.ByteLength,
                    DurationSeconds = excluded.DurationSeconds,
                    Width = excluded.Width,
                    Height = excluded.Height,
                    Sha256 = excluded.Sha256,
                    SourceSha256 = excluded.SourceSha256,
                    CreatedAt = excluded.CreatedAt";

            cmd.Parameters.AddWithValue("$itemId", derivative.ItemId.ToString("N"));
            cmd.Parameters.AddWithValue("$imageType", derivative.ImageType);
            cmd.Parameters.AddWithValue("$shortEdge", derivative.ShortEdge);
            cmd.Parameters.AddWithValue("$contentType", derivative.ContentType);
            cmd.Parameters.AddWithValue("$fileName", fileName);
            cmd.Parameters.AddWithValue("$byteLength", derivative.ByteLength);
            cmd.Parameters.AddWithValue("$durationSeconds", (object?)derivative.DurationSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$width", (object?)derivative.Width ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$height", (object?)derivative.Height ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sha256", derivative.Sha256);
            cmd.Parameters.AddWithValue("$sourceSha256", derivative.SourceSha256);
            cmd.Parameters.AddWithValue("$createdAt", derivative.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        return derivative;
    }

    /// <summary>
    /// Drops every cached rendition of an item, or of one of its slots, removing rows and files.
    /// Called whenever a master changes or goes away, so a stale size can never outlive it.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="imageType">The slot to clear, or <see langword="null"/> for every slot.</param>
    /// <returns>The number of renditions removed.</returns>
    public int DeleteDerivatives(Guid itemId, string? imageType = null)
    {
        lock (_connectionLock)
        {
            return DeleteDerivativesNoLock(itemId, imageType);
        }
    }

    /// <summary>
    /// Returns every distinct item id held by this store. Used by the orphan sweep to find
    /// rows whose Jellyfin item no longer exists — which is the whole store after a library
    /// root-folder change, since item ids are derived from paths.
    /// </summary>
    /// <returns>All distinct item ids.</returns>
    public IReadOnlyList<Guid> GetAllItemIds()
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT ItemId FROM AnimatedArtwork";

            var ids = new List<Guid>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }

    /// <summary>
    /// Gets the total number of stored artwork assets across all items and image slots.
    /// </summary>
    /// <returns>The asset count.</returns>
    public long Count()
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM AnimatedArtwork";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <summary>
    /// Maps a stored content type to the file extension used on disk.
    /// </summary>
    /// <param name="contentType">The MIME type.</param>
    /// <returns>A file extension including the leading dot.</returns>
    internal static string ExtensionFor(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "video/quicktime" => ".mov",
            "video/webm" => ".webm",
            _ => ".mp4"
        };
    }

    /// <summary>
    /// Moves every portrait album loop sitting in a pre-tall-slot slot (<c>Primary</c>, or the
    /// <c>Backdrop</c> slot the Apple backfill used for its tall variant) into the <c>Tall</c>
    /// slot, renaming the backing file to match. Shape is judged from the stored dimensions, so
    /// rows with unknown width/height are left alone, as are albums that already have a tall asset.
    /// Runs once, gated on the schema version, so a deliberately portrait upload in the square slot
    /// is not yanked away on a later restart.
    /// </summary>
    /// <returns>The number of assets moved.</returns>
    internal int ReclassifyTallAlbumArtwork()
    {
        var moved = 0;
        lock (_connectionLock)
        {
            foreach (var sourceSlot in LegacyAlbumSlots)
            {
                foreach (var candidate in GetTallAlbumAssetsNoLock(sourceSlot))
                {
                    if (GetFileNameNoLock(candidate.ItemId, ArtworkShape.TallImageType) != null)
                    {
                        _logger.LogWarning(
                            "Leaving portrait {Slot} artwork for album {ItemId} in place; a {Tall} asset already exists",
                            sourceSlot,
                            candidate.ItemId,
                            ArtworkShape.TallImageType);
                        continue;
                    }

                    var fileName = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{candidate.ItemId:N}_{ArtworkShape.TallImageType}{Path.GetExtension(candidate.FileName)}");

                    if (!TryMoveFile(
                        Path.Combine(_artworkDir, candidate.FileName),
                        Path.Combine(_artworkDir, fileName)))
                    {
                        continue;
                    }

                    using (var update = _connection.CreateCommand())
                    {
                        update.CommandText = @"
                            UPDATE AnimatedArtwork
                            SET ImageType = $tall, FileName = $fileName
                            WHERE ItemId = $itemId AND ImageType = $source";
                        update.Parameters.AddWithValue("$tall", ArtworkShape.TallImageType);
                        update.Parameters.AddWithValue("$fileName", fileName);
                        update.Parameters.AddWithValue("$itemId", candidate.ItemId.ToString("N"));
                        update.Parameters.AddWithValue("$source", sourceSlot);
                        update.ExecuteNonQuery();
                    }

                    // The source slot is empty again, so drop its negative-cache row: the Apple
                    // backfill should be free to fill it with the variant that actually belongs
                    // there rather than waiting out the retry window.
                    using (var forget = _connection.CreateCommand())
                    {
                        forget.CommandText = "DELETE FROM AppleArtworkAttempt WHERE ItemId = $itemId AND ImageType = $source";
                        forget.Parameters.AddWithValue("$itemId", candidate.ItemId.ToString("N"));
                        forget.Parameters.AddWithValue("$source", sourceSlot);
                        forget.ExecuteNonQuery();
                    }

                    moved++;
                }
            }
        }

        return moved;
    }

    private AnimatedArtworkRecord ReadRecord(SqliteDataReader reader)
    {
        var fileName = reader.GetString(4);
        return new AnimatedArtworkRecord
        {
            ItemId = Guid.Parse(reader.GetString(0)),
            ItemType = reader.GetString(1),
            ImageType = reader.GetString(2),
            ContentType = reader.GetString(3),
            FileName = fileName,
            FilePath = Path.Combine(_artworkDir, fileName),
            ByteLength = reader.GetInt64(5),
            DurationSeconds = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            Width = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            Height = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            MoovAtFront = reader.IsDBNull(9) ? null : reader.GetInt64(9) != 0,
            Sha256 = reader.GetString(10),
            UploadedAt = DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UploadedBy = reader.IsDBNull(12) ? null : reader.GetString(12)
        };
    }

    private AnimatedArtworkDerivative ReadDerivative(SqliteDataReader reader)
    {
        var fileName = reader.GetString(4);
        return new AnimatedArtworkDerivative
        {
            ItemId = Guid.Parse(reader.GetString(0)),
            ImageType = reader.GetString(1),
            ShortEdge = reader.GetInt32(2),
            ContentType = reader.GetString(3),
            FileName = fileName,
            FilePath = Path.Combine(_derivedDir, fileName),
            ByteLength = reader.GetInt64(5),
            DurationSeconds = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            Width = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            Height = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            Sha256 = reader.GetString(9),
            SourceSha256 = reader.GetString(10),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    private int DeleteDerivativesNoLock(Guid itemId, string? imageType)
    {
        // Two literal statements rather than one built by concatenation: CommandText assembled from
        // variables is what the SQL-injection analyzer exists to catch, and the exemption is not
        // worth the shortcut.
        var fileNames = new List<string>();

        using (var select = _connection.CreateCommand())
        {
            if (imageType == null)
            {
                select.CommandText = "SELECT FileName FROM AnimatedArtworkDerivative WHERE ItemId = $itemId";
            }
            else
            {
                select.CommandText = "SELECT FileName FROM AnimatedArtworkDerivative WHERE ItemId = $itemId AND ImageType = $imageType";
                select.Parameters.AddWithValue("$imageType", imageType);
            }

            select.Parameters.AddWithValue("$itemId", itemId.ToString("N"));

            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                fileNames.Add(reader.GetString(0));
            }
        }

        if (fileNames.Count == 0)
        {
            return 0;
        }

        using (var delete = _connection.CreateCommand())
        {
            if (imageType == null)
            {
                delete.CommandText = "DELETE FROM AnimatedArtworkDerivative WHERE ItemId = $itemId";
            }
            else
            {
                delete.CommandText = "DELETE FROM AnimatedArtworkDerivative WHERE ItemId = $itemId AND ImageType = $imageType";
                delete.Parameters.AddWithValue("$imageType", imageType);
            }

            delete.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
            delete.ExecuteNonQuery();
        }

        foreach (var fileName in fileNames)
        {
            TryDeleteFile(Path.Combine(_derivedDir, fileName));
        }

        return fileNames.Count;
    }

    private string? GetFileNameNoLock(Guid itemId, string imageType)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT FileName FROM AnimatedArtwork WHERE ItemId = $itemId AND ImageType = $imageType";
        cmd.Parameters.AddWithValue("$itemId", itemId.ToString("N"));
        cmd.Parameters.AddWithValue("$imageType", imageType);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    // Album assets in the given slot whose stored dimensions make them portrait. Shape is judged in
    // C# rather than SQL so the tolerance lives in exactly one place.
    private List<TallAlbumAsset> GetTallAlbumAssetsNoLock(string imageType)
    {
        var candidates = new List<TallAlbumAsset>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ItemId, FileName, Width, Height
            FROM AnimatedArtwork
            WHERE ImageType = $imageType AND ItemType = 'Album'
              AND Width IS NOT NULL AND Height IS NOT NULL";
        cmd.Parameters.AddWithValue("$imageType", imageType);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(0), out var itemId)
                || !ArtworkShape.IsTall(reader.GetInt32(2), reader.GetInt32(3)))
            {
                continue;
            }

            candidates.Add(new TallAlbumAsset(itemId, reader.GetString(1)));
        }

        return candidates;
    }

    // Renames a backing file. A file that is already gone counts as moved: the row is broken either
    // way, and re-slotting it at least keeps the index consistent. A genuine failure is reported so
    // the caller can leave the row where it is.
    private bool TryMoveFile(string sourcePath, string destinationPath)
    {
        try
        {
            if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
            }

            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to move animated artwork file {Path}", sourcePath);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to move animated artwork file {Path}", sourcePath);
            return false;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete animated artwork file {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to delete animated artwork file {Path}", path);
        }
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS AnimatedArtwork (
                ItemId TEXT NOT NULL,
                ItemType TEXT NOT NULL,
                ImageType TEXT NOT NULL,
                ContentType TEXT NOT NULL,
                FileName TEXT NOT NULL,
                ByteLength INTEGER NOT NULL,
                DurationSeconds REAL,
                Width INTEGER,
                Height INTEGER,
                MoovAtFront INTEGER,
                Sha256 TEXT NOT NULL,
                UploadedAt TEXT NOT NULL,
                UploadedBy TEXT,
                PRIMARY KEY (ItemId, ImageType)
            );

            CREATE INDEX IF NOT EXISTS IX_AnimatedArtwork_ImageType ON AnimatedArtwork (ImageType);

            CREATE TABLE IF NOT EXISTS AppleArtworkAttempt (
                ItemId TEXT NOT NULL,
                ImageType TEXT NOT NULL,
                Status TEXT NOT NULL,
                AttemptedAtTicks INTEGER NOT NULL,
                PRIMARY KEY (ItemId, ImageType)
            );";
        cmd.ExecuteNonQuery();

        using var derivatives = _connection.CreateCommand();
        derivatives.CommandText = CreateDerivativeTableSql;
        derivatives.ExecuteNonQuery();
    }

    // Brings an existing database up to CurrentSchemaVersion. A fresh database starts at 0 and runs
    // the migrations against empty tables, which is a no-op, so there is no separate "new install"
    // path to keep in step.
    private void ApplyMigrations()
    {
        var version = GetSchemaVersion();
        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        if (version < 1)
        {
            var moved = ReclassifyTallAlbumArtwork();
            if (moved > 0)
            {
                _logger.LogInformation(
                    "Moved {Count} portrait album loop(s) into the {Slot} slot",
                    moved,
                    ArtworkShape.TallImageType);
            }
        }

        if (version < 2)
        {
            var dropped = DiscardDerivativeCache();
            if (dropped > 0)
            {
                _logger.LogInformation("Discarded {Count} cached rendition(s) cut under long-edge sizing", dropped);
            }
        }

        SetSchemaVersion();
    }

    /// <summary>
    /// Empties the rendition cache. A ladder step used to name the long edge and now names the short
    /// one, so every cached rendition is filed under a number that no longer describes its pixels —
    /// and a landscape master offers fewer steps than it did, so some files have no row left to find
    /// them by. Masters are untouched; renditions rebuild on demand.
    /// </summary>
    /// <returns>The number of rendition files removed.</returns>
    internal int DiscardDerivativeCache()
    {
        lock (_connectionLock)
        {
            using (var drop = _connection.CreateCommand())
            {
                drop.CommandText = DropDerivativeTableSql;
                drop.ExecuteNonQuery();
            }

            using (var create = _connection.CreateCommand())
            {
                create.CommandText = CreateDerivativeTableSql;
                create.ExecuteNonQuery();
            }

            // Sweep the directory rather than the rows we just dropped: it holds nothing but
            // renditions, and a commit that failed part-way leaves a file no row ever pointed at.
            // Materialized, because we delete while walking.
            var files = Directory.GetFiles(_derivedDir);
            foreach (var path in files)
            {
                TryDeleteFile(path);
            }

            return files.Length;
        }
    }

    private int GetSchemaVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void SetSchemaVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = SetSchemaVersionSql;
        cmd.ExecuteNonQuery();
    }

    private readonly record struct TallAlbumAsset(Guid ItemId, string FileName);
}
