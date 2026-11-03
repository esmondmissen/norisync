using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Persists per-track waveform analysis blobs to a SQLite database. Older databases may carry
/// orphaned columns from previous schemas (Scores, Bpm, KeyRoot, KeyMode, Lufs, BeatGrid,
/// BeatGridVersion); the embedding columns (ModelVersion, Embedding, EmbeddingDim) are dropped
/// on upgrade — see <see cref="InitializeDatabase"/>.
/// </summary>
public class TrackFeatureStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly object _connectionLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackFeatureStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory path where the database file will be stored.</param>
    /// <param name="logger">Logger instance.</param>
    public TrackFeatureStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "features.db");
        _logger = logger;

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        _logger.LogInformation("Track feature database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Inserts or replaces a feature record, and replaces the row's library mappings with
    /// <paramref name="libraryIds"/>. A track may appear in multiple libraries when the same
    /// path is rooted under more than one Jellyfin library.
    /// </summary>
    /// <param name="feature">The feature record to persist.</param>
    /// <param name="libraryIds">The collection-folder ids the track is reachable through. Empty
    /// when the track has no library ancestor (rare; orphaned items still get a row but
    /// won't show up in any library-filtered query).</param>
    /// <remarks>
    /// <see cref="TrackFeature.ProcessedAt"/> is stamped here, by <see cref="SyncClock"/>, as the
    /// row commits — it is what the waveform feed is paged by, and a stamp taken earlier could
    /// land behind a cursor a client was handed before the row was saved.
    /// </remarks>
    public void Upsert(TrackFeature feature, IReadOnlyCollection<Guid> libraryIds)
    {
        lock (_connectionLock)
        {
            SyncClock.Stamp(stamp =>
            {
                feature.ProcessedAt = stamp;
                using var tx = _connection.BeginTransaction();

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        INSERT INTO TrackFeatures (ItemId, Waveform, WaveformVersion, SourceMtime, ProcessedAt)
                        VALUES ($itemId, $waveform, $waveformVersion, $sourceMtime, $processedAt)
                        ON CONFLICT(ItemId) DO UPDATE SET
                            Waveform = excluded.Waveform,
                            WaveformVersion = excluded.WaveformVersion,
                            SourceMtime = excluded.SourceMtime,
                            ProcessedAt = excluded.ProcessedAt";

                    cmd.Parameters.AddWithValue("$itemId", feature.ItemId.ToString());
                    cmd.Parameters.AddWithValue("$waveform", (object?)feature.Waveform ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$waveformVersion", (object?)feature.WaveformVersion ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$sourceMtime", feature.SourceMtime);
                    cmd.Parameters.AddWithValue("$processedAt", stamp.ToUnixTimeMilliseconds());

                    cmd.ExecuteNonQuery();
                }

                ReplaceLibrariesInTransaction(feature.ItemId, libraryIds, tx);

                tx.Commit();
            });
        }
    }

    /// <summary>
    /// Replaces the library mappings for a track without touching its feature columns. Used by
    /// the startup backfill, which retrofits library ids onto rows that were written before the
    /// join table existed.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="libraryIds">Library ids to associate with the row. Empty clears existing mappings.</param>
    /// <returns>True when a feature row exists for <paramref name="itemId"/> (mappings were applied).</returns>
    public bool SetLibraries(Guid itemId, IReadOnlyCollection<Guid> libraryIds)
    {
        lock (_connectionLock)
        {
            using var tx = _connection.BeginTransaction();

            using (var check = _connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT 1 FROM TrackFeatures WHERE ItemId = $itemId";
                check.Parameters.AddWithValue("$itemId", itemId.ToString());
                if (check.ExecuteScalar() == null)
                {
                    return false;
                }
            }

            ReplaceLibrariesInTransaction(itemId, libraryIds, tx);
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// Returns ids of feature rows that have no entry in the library join table. Used at startup
    /// to drive a one-shot backfill so existing rows become filterable without waiting for the
    /// next waveform analysis run.
    /// </summary>
    /// <returns>Item ids missing library mappings.</returns>
    public IReadOnlyList<Guid> GetItemIdsMissingLibraries()
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT TF.ItemId FROM TrackFeatures TF
                WHERE NOT EXISTS (SELECT 1 FROM TrackFeatureLibraries L WHERE L.ItemId = TF.ItemId)";

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

    private void ReplaceLibrariesInTransaction(Guid itemId, IReadOnlyCollection<Guid> libraryIds, SqliteTransaction tx)
    {
        using (var del = _connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM TrackFeatureLibraries WHERE ItemId = $itemId";
            del.Parameters.AddWithValue("$itemId", itemId.ToString());
            del.ExecuteNonQuery();
        }

        if (libraryIds.Count == 0)
        {
            return;
        }

        using var ins = _connection.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT OR IGNORE INTO TrackFeatureLibraries (ItemId, LibraryId) VALUES ($itemId, $libraryId)";
        var itemIdParam = ins.CreateParameter();
        itemIdParam.ParameterName = "$itemId";
        itemIdParam.Value = itemId.ToString();
        ins.Parameters.Add(itemIdParam);

        var libraryIdParam = ins.CreateParameter();
        libraryIdParam.ParameterName = "$libraryId";
        ins.Parameters.Add(libraryIdParam);

        foreach (var libraryId in libraryIds)
        {
            if (libraryId == Guid.Empty)
            {
                continue;
            }

            libraryIdParam.Value = libraryId.ToString();
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns the feature record for a single track, or null if none exists.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <returns>The feature record, or null.</returns>
    public TrackFeature? Get(Guid itemId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT ItemId, SourceMtime, ProcessedAt, Waveform, WaveformVersion
                FROM TrackFeatures WHERE ItemId = $itemId";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString());

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }
    }

    /// <summary>
    /// Checks whether a non-stale waveform blob exists for the given track. The stored
    /// <c>WaveformVersion</c> and <c>SourceMtime</c> must both match, and the blob itself
    /// must be non-null (a prior analysis failure may have left the column empty even
    /// though the version string was written).
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="waveformVersion">Expected waveform analyzer version.</param>
    /// <param name="sourceMtime">Current source file mtime in ticks.</param>
    /// <returns>True when a matching waveform exists.</returns>
    public bool HasFreshWaveform(Guid itemId, string waveformVersion, long sourceMtime)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT 1 FROM TrackFeatures
                WHERE ItemId = $itemId
                  AND WaveformVersion = $waveformVersion
                  AND SourceMtime = $sourceMtime
                  AND Waveform IS NOT NULL";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString());
            cmd.Parameters.AddWithValue("$waveformVersion", waveformVersion);
            cmd.Parameters.AddWithValue("$sourceMtime", sourceMtime);
            return cmd.ExecuteScalar() != null;
        }
    }

    /// <summary>
    /// Gets the number of feature records currently persisted.
    /// </summary>
    /// <returns>The row count.</returns>
    public long Count()
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM TrackFeatures";
            var result = cmd.ExecuteScalar();
            return result == null ? 0 : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Enumerates feature records whose <see cref="TrackFeature.ProcessedAt"/> is strictly after
    /// <paramref name="since"/> and at-or-before <paramref name="until"/>, restricted to tracks
    /// reachable through any of <paramref name="libraryIds"/>, ordered ascending.
    /// </summary>
    /// <param name="since">Exclusive lower bound. Pass <see cref="DateTimeOffset.MinValue"/> for no lower bound.</param>
    /// <param name="until">Inclusive upper bound, or <see langword="null"/> for no upper bound.</param>
    /// <param name="libraryIds">Library ids to filter on. Must be non-empty.</param>
    /// <param name="limit">Maximum rows to return.</param>
    /// <param name="offset">Number of matching rows to skip.</param>
    /// <returns>The matching records.</returns>
    public IReadOnlyList<TrackFeature> GetProcessedSince(
        DateTimeOffset since,
        DateTimeOffset? until,
        IReadOnlyCollection<Guid> libraryIds,
        int limit,
        int offset)
    {
        if (libraryIds.Count == 0)
        {
            return Array.Empty<TrackFeature>();
        }

        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            var (libraryClause, libraryParamNames) = BuildLibraryFilterClause(libraryIds);

            var sql = @"
                SELECT ItemId, SourceMtime, ProcessedAt, Waveform, WaveformVersion
                FROM TrackFeatures
                WHERE ProcessedAt > $since
                  AND " + libraryClause;
            if (until.HasValue)
            {
                sql += " AND ProcessedAt <= $until";
                cmd.Parameters.AddWithValue("$until", until.Value.ToUnixTimeMilliseconds());
            }

            sql += " ORDER BY ProcessedAt ASC LIMIT $limit OFFSET $offset";
#pragma warning disable CA2100 // SQL is concatenated only with self-generated parameter names; values bind as parameters.
            cmd.CommandText = sql;
#pragma warning restore CA2100
            cmd.Parameters.AddWithValue("$since", since == DateTimeOffset.MinValue ? 0 : since.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
            BindLibraryParams(cmd, libraryParamNames, libraryIds);

            var records = new List<TrackFeature>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                records.Add(ReadRecord(reader));
            }

            return records;
        }
    }

    /// <summary>
    /// Gets the number of feature records processed strictly after <paramref name="since"/>
    /// and at-or-before <paramref name="until"/>, restricted to tracks reachable through any of
    /// <paramref name="libraryIds"/>.
    /// </summary>
    /// <param name="since">Exclusive lower bound.</param>
    /// <param name="until">Inclusive upper bound, or <see langword="null"/> for no upper bound.</param>
    /// <param name="libraryIds">Library ids to filter on. Must be non-empty.</param>
    /// <returns>The matching row count.</returns>
    public long CountProcessedSince(DateTimeOffset since, DateTimeOffset? until, IReadOnlyCollection<Guid> libraryIds)
    {
        if (libraryIds.Count == 0)
        {
            return 0;
        }

        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            var (libraryClause, libraryParamNames) = BuildLibraryFilterClause(libraryIds);

            var sql = "SELECT COUNT(*) FROM TrackFeatures WHERE ProcessedAt > $since AND " + libraryClause;
            if (until.HasValue)
            {
                sql += " AND ProcessedAt <= $until";
                cmd.Parameters.AddWithValue("$until", until.Value.ToUnixTimeMilliseconds());
            }

#pragma warning disable CA2100 // SQL is concatenated only with self-generated parameter names; values bind as parameters.
            cmd.CommandText = sql;
#pragma warning restore CA2100
            cmd.Parameters.AddWithValue("$since", since == DateTimeOffset.MinValue ? 0 : since.ToUnixTimeMilliseconds());
            BindLibraryParams(cmd, libraryParamNames, libraryIds);
            var result = cmd.ExecuteScalar();
            return result == null ? 0 : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static (string Clause, IReadOnlyList<string> ParamNames) BuildLibraryFilterClause(IReadOnlyCollection<Guid> libraryIds)
    {
        var paramNames = new List<string>(libraryIds.Count);
        for (var i = 0; i < libraryIds.Count; i++)
        {
            paramNames.Add("$lib" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var clause = "EXISTS (SELECT 1 FROM TrackFeatureLibraries L WHERE L.ItemId = TrackFeatures.ItemId AND L.LibraryId IN ("
            + string.Join(", ", paramNames)
            + "))";
        return (clause, paramNames);
    }

    private static void BindLibraryParams(SqliteCommand cmd, IReadOnlyList<string> paramNames, IReadOnlyCollection<Guid> libraryIds)
    {
        var i = 0;
        foreach (var id in libraryIds)
        {
            cmd.Parameters.AddWithValue(paramNames[i], id.ToString());
            i++;
        }
    }

    /// <summary>
    /// Atomically adds <paramref name="delta"/> to the lifetime processed counter and stamps
    /// the counting-since timestamp the first time the counter is written.
    /// </summary>
    /// <param name="delta">The amount to add (typically 1).</param>
    public void IncrementLifetimeProcessed(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        lock (_connectionLock)
        {
            using (var init = _connection.CreateCommand())
            {
                init.CommandText = @"
                    INSERT INTO Metrics (Key, Value) VALUES ('lifetime_counting_since', $now)
                    ON CONFLICT(Key) DO NOTHING";
                init.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                init.ExecuteNonQuery();
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Metrics (Key, Value) VALUES ('lifetime_processed', $delta)
                ON CONFLICT(Key) DO UPDATE SET Value = Value + excluded.Value";
            cmd.Parameters.AddWithValue("$delta", (long)delta);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Gets the cumulative number of successful processing operations across all runs.
    /// </summary>
    /// <returns>The counter value, or 0 if it has never been written.</returns>
    public long GetLifetimeProcessed()
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Metrics WHERE Key = 'lifetime_processed'";
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value
                ? 0
                : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Gets the timestamp at which the lifetime processed counter started accumulating, or
    /// <see langword="null"/> if no successful operations have been recorded since the counter
    /// was introduced.
    /// </summary>
    /// <returns>The timestamp, or <see langword="null"/>.</returns>
    public DateTimeOffset? GetLifetimeCountingSince()
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Metrics WHERE Key = 'lifetime_counting_since'";
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value)
            {
                return null;
            }

            var unixMs = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
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
            cmd.CommandText = "SELECT DISTINCT ItemId FROM TrackFeatures";

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
    /// Removes a single track's features.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    public void Delete(Guid itemId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();

            // The join rows have to go too — leaving them behind orphans a row per library
            // the track belonged to, which the library-scoped feed then filters against.
            cmd.CommandText = @"
                DELETE FROM TrackFeatureLibraries WHERE ItemId = $itemId;
                DELETE FROM TrackFeatures WHERE ItemId = $itemId;";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes all feature records.
    /// </summary>
    public void Clear()
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM TrackFeatures";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("All track features cleared");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
        }
    }

    private static TrackFeature ReadRecord(SqliteDataReader reader)
    {
        var waveform = reader.IsDBNull(3) ? null : (byte[])reader["Waveform"];
        var waveformVersion = reader.IsDBNull(4) ? null : reader.GetString(4);

        return new TrackFeature
        {
            ItemId = Guid.Parse(reader.GetString(0)),
            SourceMtime = reader.GetInt64(1),
            ProcessedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            Waveform = waveform,
            WaveformVersion = waveformVersion
        };
    }

    private void InitializeDatabase()
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS TrackFeatures (
                    ItemId TEXT PRIMARY KEY,
                    Waveform BLOB,
                    WaveformVersion TEXT,
                    SourceMtime INTEGER NOT NULL,
                    ProcessedAt INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_TrackFeatures_ProcessedAt ON TrackFeatures(ProcessedAt);

                CREATE TABLE IF NOT EXISTS TrackFeatureLibraries (
                    ItemId TEXT NOT NULL,
                    LibraryId TEXT NOT NULL,
                    PRIMARY KEY (ItemId, LibraryId),
                    FOREIGN KEY (ItemId) REFERENCES TrackFeatures(ItemId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_TrackFeatureLibraries_LibraryId ON TrackFeatureLibraries(LibraryId);

                CREATE TABLE IF NOT EXISTS Metrics (
                    Key TEXT PRIMARY KEY,
                    Value INTEGER NOT NULL
                );";
            cmd.ExecuteNonQuery();
        }

        // Migrate databases that predate the Waveform columns. Pre-existing rows in older
        // schemas may carry orphaned columns (Scores, Bpm, KeyRoot, KeyMode, Lufs, BeatGrid,
        // BeatGridVersion) — those are left in place but never read or written.
        var existing = GetTrackFeaturesColumns();
        if (!existing.Contains("Waveform"))
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE TrackFeatures ADD COLUMN Waveform BLOB";
            alter.ExecuteNonQuery();
        }

        if (!existing.Contains("WaveformVersion"))
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE TrackFeatures ADD COLUMN WaveformVersion TEXT";
            alter.ExecuteNonQuery();
        }

        // Drop the embedding columns from databases written before embeddings were removed.
        // These were NOT NULL, so leaving them would break inserts that no longer supply them.
        // The index over ModelVersion must go first — DROP COLUMN fails on an indexed column.
        if (existing.Contains("Embedding") || existing.Contains("EmbeddingDim") || existing.Contains("ModelVersion"))
        {
            using (var dropIndex = _connection.CreateCommand())
            {
                dropIndex.CommandText = "DROP INDEX IF EXISTS IX_TrackFeatures_ModelVersion";
                dropIndex.ExecuteNonQuery();
            }

            foreach (var column in new[] { "Embedding", "EmbeddingDim", "ModelVersion" })
            {
                if (!existing.Contains(column))
                {
                    continue;
                }

                using var alter = _connection.CreateCommand();
#pragma warning disable CA2100 // Column name comes from a hardcoded literal array, not user input.
                alter.CommandText = $"ALTER TABLE TrackFeatures DROP COLUMN {column}";
#pragma warning restore CA2100
                alter.ExecuteNonQuery();
            }

            _logger.LogInformation("Dropped legacy embedding columns from TrackFeatures");
        }

        // Before anything is stamped, so stamps keep increasing across a restart.
        using (var latest = _connection.CreateCommand())
        {
            latest.CommandText = "SELECT MAX(ProcessedAt) FROM TrackFeatures";
            if (latest.ExecuteScalar() is long latestMs)
            {
                SyncClock.Observe(DateTimeOffset.FromUnixTimeMilliseconds(latestMs));
            }
        }

        ConvertLegacyWaveforms();
    }

    /// <summary>
    /// Converts any 60 Hz (v5) waveform rows left over from before the 30 Hz switch to the current
    /// wire format in place, by downsampling the stored blob rather than re-decoding the audio.
    /// This is exact (see <see cref="WaveformAnalyzer.ConvertLegacyToCurrent"/>) and bumps each
    /// row's <c>ProcessedAt</c> so clients pick the new blob up on their next waveform sync. Because
    /// the conversion is exact, the row is stamped with the current analyzer
    /// <see cref="WaveformAnalyzer.Version"/>, so the scheduled task treats it as up to date and does
    /// not re-analyze it. Rows that can't be recognised are left untouched and re-analyzed by the
    /// scheduled task instead.
    /// </summary>
    private void ConvertLegacyWaveforms()
    {
        var ids = new List<string>();
        using (var sel = _connection.CreateCommand())
        {
            sel.CommandText = "SELECT ItemId FROM TrackFeatures WHERE WaveformVersion = $v AND Waveform IS NOT NULL";
            sel.Parameters.AddWithValue("$v", WaveformAnalyzer.LegacyHalfRateVersion);
            using var reader = sel.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        if (ids.Count == 0)
        {
            return;
        }

        var converted = 0;
        SyncClock.Stamp(stamp =>
        {
            using var tx = _connection.BeginTransaction();
            foreach (var id in ids)
            {
                byte[]? blob;
                using (var get = _connection.CreateCommand())
                {
                    get.Transaction = tx;
                    get.CommandText = "SELECT Waveform FROM TrackFeatures WHERE ItemId = $id";
                    get.Parameters.AddWithValue("$id", id);
                    blob = get.ExecuteScalar() as byte[];
                }

                var v6 = WaveformAnalyzer.ConvertLegacyToCurrent(blob);
                if (v6 == null)
                {
                    // Unrecognised blob — leave it stale so the scheduled task re-analyzes it.
                    continue;
                }

                using var upd = _connection.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = @"
                    UPDATE TrackFeatures
                    SET Waveform = $w, WaveformVersion = $ver, ProcessedAt = $now
                    WHERE ItemId = $id";
                upd.Parameters.AddWithValue("$w", v6);
                upd.Parameters.AddWithValue("$ver", WaveformAnalyzer.Version);
                upd.Parameters.AddWithValue("$now", stamp.ToUnixTimeMilliseconds());
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
                converted++;
            }

            tx.Commit();
        });

        if (converted > 0)
        {
            _logger.LogInformation("Converted {Count} waveform(s) from 60 Hz to 30 Hz in place", converted);
        }
    }

    private HashSet<string> GetTrackFeaturesColumns()
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(TrackFeatures)";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
