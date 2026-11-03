using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Persists change records to a SQLite database.
/// </summary>
public class ChangeTrackingStore : IDisposable
{
    // Set once the first resolution is recorded, so "never recorded" is told apart from an
    // empty library's empty table.
    private const string ArtistResolutionSavedKey = "ArtistResolutionSaved";

    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly object _connectionLock = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeTrackingStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory path where the database file will be stored.</param>
    /// <param name="logger">Logger instance.</param>
    public ChangeTrackingStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "changes.db");
        _logger = logger;

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();

        using (var latest = _connection.CreateCommand())
        {
            latest.CommandText = "SELECT MAX(Timestamp) FROM Changes";
            if (latest.ExecuteScalar() is long latestMs)
            {
                SyncClock.Observe(DateTimeOffset.FromUnixTimeMilliseconds(latestMs));
            }
        }

        _logger.LogInformation("Change tracking database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Records a single change. Convenience wrapper around <see cref="AddChanges"/>.
    /// </summary>
    /// <param name="record">The change record to persist.</param>
    public void AddChange(ChangeRecord record)
    {
        AddChanges(new[] { record });
    }

    /// <summary>
    /// Records a batch of changes in a single transaction. Callers are expected to coalesce
    /// duplicate-id events (last-write-wins) before calling, so that a long library scan
    /// doesn't translate 1:1 into rows.
    /// </summary>
    /// <remarks>
    /// The batch is stamped here, by <see cref="SyncClock"/>, as it commits; whatever
    /// <see cref="ChangeRecord.Timestamp"/> the records carried is replaced. A stamp taken when
    /// the event fired could land behind a cursor a client was handed before the batch was
    /// saved, and that client would never see it.
    /// </remarks>
    /// <param name="records">The change records to persist.</param>
    public void AddChanges(IReadOnlyCollection<ChangeRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        lock (_connectionLock)
        {
            SyncClock.Stamp(stamp =>
            {
                using var tx = _connection.BeginTransaction();
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO Changes (ItemId, ItemName, ItemType, ChangeType, Timestamp, AlbumName, ArtistNames)
                    VALUES ($itemId, $itemName, $itemType, $changeType, $timestamp, $albumName, $artistNames)";

                var pItemId = AddParameter(cmd, "$itemId");
                var pItemName = AddParameter(cmd, "$itemName");
                var pItemType = AddParameter(cmd, "$itemType");
                var pChangeType = AddParameter(cmd, "$changeType");
                var pTimestamp = AddParameter(cmd, "$timestamp");
                var pAlbumName = AddParameter(cmd, "$albumName");
                var pArtistNames = AddParameter(cmd, "$artistNames");
                pTimestamp.Value = stamp.ToUnixTimeMilliseconds();

                foreach (var record in records)
                {
                    record.Timestamp = stamp;
                    pItemId.Value = record.ItemId.ToString();
                    pItemName.Value = record.ItemName;
                    pItemType.Value = record.ItemType.ToString();
                    pChangeType.Value = record.ChangeType.ToString();
                    pAlbumName.Value = (object?)record.AlbumName ?? DBNull.Value;
                    pArtistNames.Value = record.ArtistNames != null
                        ? JsonSerializer.Serialize(record.ArtistNames, _jsonOptions)
                        : DBNull.Value;

                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            });
        }
    }

    /// <summary>
    /// Queries change records with optional bounds and type filters.
    /// </summary>
    /// <param name="since">Exclusive lower bound on <see cref="ChangeRecord.Timestamp"/>.</param>
    /// <param name="until">Inclusive upper bound on <see cref="ChangeRecord.Timestamp"/>. Pass the value returned by <c>/timestamp</c> at the start of a sync session for snapshot-isolated paging.</param>
    /// <param name="itemTypes">Restrict results to these item types.</param>
    /// <returns>A response containing matching change records ordered by timestamp ascending.</returns>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Query is composed from constant fragments; the only dynamic part is parameter placeholder names generated from the itemTypes array length, never user input.")]
    public ChangesResponse GetChanges(DateTimeOffset? since, DateTimeOffset? until, TrackedItemType[]? itemTypes)
    {
        var sql = new StringBuilder("SELECT ItemId, ItemName, ItemType, ChangeType, Timestamp, AlbumName, ArtistNames FROM Changes");
        var clauses = new List<string>();
        using var cmd = _connection.CreateCommand();

        if (since.HasValue)
        {
            clauses.Add("Timestamp > $since");
            cmd.Parameters.AddWithValue("$since", since.Value.ToUnixTimeMilliseconds());
        }

        if (until.HasValue)
        {
            clauses.Add("Timestamp <= $until");
            cmd.Parameters.AddWithValue("$until", until.Value.ToUnixTimeMilliseconds());
        }

        if (itemTypes != null && itemTypes.Length > 0)
        {
            var placeholders = new string[itemTypes.Length];
            for (var i = 0; i < itemTypes.Length; i++)
            {
                var p = $"$type{i}";
                placeholders[i] = p;
                cmd.Parameters.AddWithValue(p, itemTypes[i].ToString());
            }

            clauses.Add($"ItemType IN ({string.Join(",", placeholders)})");
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ");
            sql.Append(string.Join(" AND ", clauses));
        }

        sql.Append(" ORDER BY Timestamp ASC");
        cmd.CommandText = sql.ToString();

        List<ChangeRecord> records;
        lock (_connectionLock)
        {
            records = ExecuteQuery(cmd);
        }

        return new ChangesResponse
        {
            Changes = records,
            TotalCount = records.Count,
            LatestTimestamp = records.Count > 0 ? records[^1].Timestamp : null
        };
    }

    /// <summary>
    /// Removes all change records older than the specified cutoff and advances the
    /// minimum-safe-since watermark so cursor-expired requests can be detected.
    /// </summary>
    /// <param name="cutoff">Records older than this timestamp will be removed.</param>
    public void PurgeOlderThan(DateTimeOffset cutoff)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Changes WHERE Timestamp < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
            var deleted = cmd.ExecuteNonQuery();

            if (deleted > 0)
            {
                _logger.LogInformation("Purged {Count} expired change records", deleted);
            }

            AdvanceMinSafeSinceLocked(cutoff);
        }
    }

    /// <summary>
    /// Gets the oldest server timestamp clients may still pass as <c>since</c> without risking
    /// missed changes. <see langword="null"/> means no purge has ever run and any cursor is safe.
    /// </summary>
    /// <returns>The watermark, or <see langword="null"/>.</returns>
    public DateTimeOffset? GetMinSafeSince()
    {
        object? result;
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Metadata WHERE Key = 'MinSafeSince'";
            result = cmd.ExecuteScalar();
        }

        if (result == null || result == DBNull.Value)
        {
            return null;
        }

        if (result is long asLong)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(asLong);
        }

        if (result is string asString && long.TryParse(asString, out var parsed))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(parsed);
        }

        return null;
    }

    /// <summary>
    /// Reads a value from the store's key/value metadata.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or <see langword="null"/> when unset.</returns>
    public string? GetMetadata(string key)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Metadata WHERE Key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>
    /// Writes a value to the store's key/value metadata.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public void SetMetadata(string key, string value)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Metadata (Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes all change records and advances the watermark to now, so any subsequent
    /// client pass with an older cursor receives a cursor-expired signal.
    /// </summary>
    public void Clear()
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Changes";
            cmd.ExecuteNonQuery();
            AdvanceMinSafeSinceLocked(DateTimeOffset.UtcNow);
            _logger.LogInformation("All change records cleared");
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

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS Changes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ItemId TEXT NOT NULL,
                ItemName TEXT NOT NULL,
                ItemType TEXT NOT NULL,
                ChangeType TEXT NOT NULL,
                Timestamp INTEGER NOT NULL,
                AlbumName TEXT,
                ArtistNames TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_Changes_Timestamp ON Changes(Timestamp);
            CREATE INDEX IF NOT EXISTS IX_Changes_ItemType ON Changes(ItemType);
            CREATE INDEX IF NOT EXISTS IX_Changes_ItemId ON Changes(ItemId);

            CREATE TABLE IF NOT EXISTS Metadata (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS KnownItems (
                ItemId TEXT NOT NULL,
                ItemType TEXT NOT NULL,
                DateLastSaved INTEGER NOT NULL,
                PRIMARY KEY (ItemId, ItemType)
            );

            CREATE INDEX IF NOT EXISTS IX_KnownItems_ItemType ON KnownItems(ItemType);

            CREATE TABLE IF NOT EXISTS ArtistResolution (
                IsCleanName INTEGER NOT NULL,
                Name TEXT NOT NULL,
                ArtistId TEXT NOT NULL,
                PRIMARY KEY (IsCleanName, Name)
            );";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the artist-name resolution last recorded by <see cref="ReplaceArtistResolution"/>.
    /// </summary>
    /// <returns>The entries, or <see langword="null"/> when none has ever been recorded.</returns>
    public IReadOnlyList<ArtistResolutionEntry>? GetArtistResolution()
    {
        if (GetMetadata(ArtistResolutionSavedKey) == null)
        {
            return null;
        }

        var result = new List<ArtistResolutionEntry>();
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT IsCleanName, Name, ArtistId FROM ArtistResolution";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ArtistResolutionEntry(reader.GetInt64(0) != 0, reader.GetString(1), Guid.Parse(reader.GetString(2))));
            }
        }

        return result;
    }

    /// <summary>
    /// Atomically replaces the recorded artist-name resolution.
    /// </summary>
    /// <param name="entries">The entries, from <see cref="ArtistNameLookup.GetEntries"/>.</param>
    public void ReplaceArtistResolution(IEnumerable<ArtistResolutionEntry> entries)
    {
        lock (_connectionLock)
        {
            using var tx = _connection.BeginTransaction();

            using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM ArtistResolution";
                del.ExecuteNonQuery();
            }

            using (var ins = _connection.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO ArtistResolution (IsCleanName, Name, ArtistId) VALUES ($isCleanName, $name, $artistId)";
                var pIsCleanName = AddParameter(ins, "$isCleanName");
                var pName = AddParameter(ins, "$name");
                var pArtistId = AddParameter(ins, "$artistId");

                foreach (var entry in entries)
                {
                    pIsCleanName.Value = entry.IsCleanName ? 1 : 0;
                    pName.Value = entry.Name;
                    pArtistId.Value = entry.ArtistId.ToString();
                    ins.ExecuteNonQuery();
                }
            }

            using (var saved = _connection.CreateCommand())
            {
                saved.Transaction = tx;
                saved.CommandText = @"
                    INSERT INTO Metadata (Key, Value) VALUES ($key, '1')
                    ON CONFLICT(Key) DO NOTHING;";
                saved.Parameters.AddWithValue("$key", ArtistResolutionSavedKey);
                saved.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Inserts or updates a snapshot row for an item known to exist in the Jellyfin library.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="itemType">The tracked item type.</param>
    /// <param name="dateLastSaved">The item's last-saved timestamp from Jellyfin.</param>
    public void UpsertKnownItem(Guid itemId, TrackedItemType itemType, DateTimeOffset dateLastSaved)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO KnownItems (ItemId, ItemType, DateLastSaved)
                VALUES ($itemId, $itemType, $dateLastSaved)
                ON CONFLICT(ItemId, ItemType) DO UPDATE SET DateLastSaved = excluded.DateLastSaved";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString());
            cmd.Parameters.AddWithValue("$itemType", itemType.ToString());
            cmd.Parameters.AddWithValue("$dateLastSaved", dateLastSaved.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes a snapshot row for an item that has been deleted from the Jellyfin library.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="itemType">The tracked item type.</param>
    public void RemoveKnownItem(Guid itemId, TrackedItemType itemType)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM KnownItems WHERE ItemId = $itemId AND ItemType = $itemType";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString());
            cmd.Parameters.AddWithValue("$itemType", itemType.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns the snapshot rows for the given item type as a dictionary keyed by item id.
    /// </summary>
    /// <param name="itemType">The tracked item type.</param>
    /// <returns>A dictionary of item id to last-saved timestamp.</returns>
    public Dictionary<Guid, DateTimeOffset> GetKnownItems(TrackedItemType itemType)
    {
        var result = new Dictionary<Guid, DateTimeOffset>();
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT ItemId, DateLastSaved FROM KnownItems WHERE ItemType = $itemType";
            cmd.Parameters.AddWithValue("$itemType", itemType.ToString());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
                result[id] = ts;
            }
        }

        return result;
    }

    /// <summary>
    /// Atomically replaces all snapshot rows for the given item type with the supplied set.
    /// Used by startup reconciliation to self-heal any drift between the snapshot and the live library.
    /// </summary>
    /// <param name="itemType">The tracked item type.</param>
    /// <param name="rows">The new set of (item id, last-saved) rows.</param>
    public void ReplaceKnownItems(TrackedItemType itemType, IEnumerable<(Guid ItemId, DateTimeOffset DateLastSaved)> rows)
    {
        lock (_connectionLock)
        {
            using var tx = _connection.BeginTransaction();

            using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM KnownItems WHERE ItemType = $itemType";
                del.Parameters.AddWithValue("$itemType", itemType.ToString());
                del.ExecuteNonQuery();
            }

            using (var ins = _connection.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO KnownItems (ItemId, ItemType, DateLastSaved) VALUES ($itemId, $itemType, $dateLastSaved)";
                var pItemId = AddParameter(ins, "$itemId");
                var pItemType = AddParameter(ins, "$itemType");
                pItemType.Value = itemType.ToString();
                var pDateLastSaved = AddParameter(ins, "$dateLastSaved");

                foreach (var row in rows)
                {
                    pItemId.Value = row.ItemId.ToString();
                    pDateLastSaved.Value = row.DateLastSaved.ToUnixTimeMilliseconds();
                    ins.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Returns the total number of snapshot rows across all item types. A return value of zero
    /// signals first-run state (or a fresh DB after upgrade), which callers use to decide whether
    /// to bootstrap silently rather than emitting synthetic Added records for the entire library.
    /// </summary>
    /// <returns>The count.</returns>
    public long CountKnownItems()
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM KnownItems";
            var result = cmd.ExecuteScalar();
            return result is long l ? l : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void AdvanceMinSafeSinceLocked(DateTimeOffset cutoff)
    {
        var cutoffMs = cutoff.ToUnixTimeMilliseconds();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Metadata (Key, Value) VALUES ('MinSafeSince', $value)
            ON CONFLICT(Key) DO UPDATE SET Value = CAST(MAX(CAST(Metadata.Value AS INTEGER), CAST(excluded.Value AS INTEGER)) AS TEXT);";
        cmd.Parameters.AddWithValue("$value", cutoffMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static SqliteParameter AddParameter(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    private static List<ChangeRecord> ExecuteQuery(SqliteCommand cmd)
    {
        var records = new List<ChangeRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private static ChangeRecord ReadRecord(SqliteDataReader reader)
    {
        var artistNamesJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        List<string>? artistNames = null;
        if (artistNamesJson != null)
        {
            artistNames = JsonSerializer.Deserialize<List<string>>(artistNamesJson, _jsonOptions);
        }

        return new ChangeRecord
        {
            ItemId = Guid.Parse(reader.GetString(0)),
            ItemName = reader.GetString(1),
            ItemType = Enum.Parse<TrackedItemType>(reader.GetString(2)),
            ChangeType = Enum.Parse<ChangeType>(reader.GetString(3)),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            AlbumName = reader.IsDBNull(5) ? null : reader.GetString(5),
            ArtistNames = artistNames
        };
    }
}
