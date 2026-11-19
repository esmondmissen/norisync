using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Persists each user's listens — every track a Nori device played past its play threshold — so
/// all of a user's devices share one listening history. Devices upload their own listens and read
/// the ones made elsewhere.
/// </summary>
/// <remarks>
/// The feed cursor is <c>Seq</c>, a number this store assigns on insert, never a time: a listen
/// made offline is uploaded long after it happened, and a time cursor would skip it on every
/// device that had already read past that moment.
/// </remarks>
public sealed class ListenHistoryStore : IDisposable
{
    private const string EpochKey = "Epoch";

    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly object _connectionLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ListenHistoryStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory path where the database file will be stored.</param>
    /// <param name="logger">Logger instance.</param>
    public ListenHistoryStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "listens.db");
        _logger = logger;

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        Epoch = LoadOrMintEpoch();
        _logger.LogInformation("Listen history database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Gets the identity of this store, minted when its database is created. Sequence numbers
    /// only mean something within one epoch: a client holding a cursor from another epoch must
    /// start again from 0 and re-upload its own listens (the store it knew is gone).
    /// </summary>
    public string Epoch { get; }

    /// <summary>
    /// Stores listens for a user. A listen whose id the user already has is left as it is, so a
    /// retried upload changes nothing.
    /// </summary>
    /// <param name="userId">The user the listens belong to.</param>
    /// <param name="listens">The listens to store.</param>
    /// <returns>How many were new.</returns>
    public int Add(Guid userId, IReadOnlyList<ListenRecord> listens)
    {
        ArgumentNullException.ThrowIfNull(listens);

        var added = 0;
        lock (_connectionLock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO Listens (UserId, ListenId, ItemId, PlayedAt, DeviceId, ContextType, ContextId)
                VALUES ($user, $listen, $item, $playedAt, $device, $contextType, $contextId);";

            var user = cmd.Parameters.Add("$user", SqliteType.Text);
            var listen = cmd.Parameters.Add("$listen", SqliteType.Text);
            var item = cmd.Parameters.Add("$item", SqliteType.Text);
            var playedAt = cmd.Parameters.Add("$playedAt", SqliteType.Integer);
            var device = cmd.Parameters.Add("$device", SqliteType.Text);
            var contextType = cmd.Parameters.Add("$contextType", SqliteType.Text);
            var contextId = cmd.Parameters.Add("$contextId", SqliteType.Text);

            user.Value = FormatId(userId);
            foreach (var record in listens)
            {
                listen.Value = record.ListenId;
                item.Value = FormatId(record.ItemId);
                playedAt.Value = record.PlayedAt;
                device.Value = record.DeviceId;
                contextType.Value = record.ContextType;
                contextId.Value = (object?)record.ContextId ?? DBNull.Value;
                added += cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return added;
    }

    /// <summary>
    /// Returns a user's listens made on devices other than <paramref name="excludingDeviceId"/>,
    /// with a sequence above <paramref name="since"/>, oldest first.
    /// </summary>
    /// <param name="userId">The user whose listens to read.</param>
    /// <param name="excludingDeviceId">The calling device, whose own listens are left out.</param>
    /// <param name="since">The cursor from the previous page; 0 to read from the start.</param>
    /// <param name="limit">The most listens to return.</param>
    /// <returns>The page. Its cursor moves past the caller's own listens too.</returns>
    public ListenPage GetSince(Guid userId, string excludingDeviceId, long since, int limit)
    {
        var listens = new List<ListenRecord>();
        long lastSeq = since;
        var hasMore = false;

        lock (_connectionLock)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT Seq, ListenId, ItemId, PlayedAt, DeviceId, ContextType, ContextId
                    FROM Listens
                    WHERE UserId = $user AND Seq > $since AND DeviceId <> $device
                    ORDER BY Seq
                    LIMIT $limit;";
                cmd.Parameters.AddWithValue("$user", FormatId(userId));
                cmd.Parameters.AddWithValue("$since", since);
                cmd.Parameters.AddWithValue("$device", excludingDeviceId);
                cmd.Parameters.AddWithValue("$limit", limit + 1);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (listens.Count == limit)
                    {
                        hasMore = true;
                        break;
                    }

                    lastSeq = reader.GetInt64(0);
                    listens.Add(new ListenRecord(
                        reader.GetString(1),
                        Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6)));
                }
            }

            if (!hasMore)
            {
                // Nothing else of another device's is waiting, so step over the caller's own rows
                // as well: the next read starts past everything stored so far.
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT MAX(Seq) FROM Listens WHERE UserId = $user AND Seq > $since";
                cmd.Parameters.AddWithValue("$user", FormatId(userId));
                cmd.Parameters.AddWithValue("$since", since);
                if (cmd.ExecuteScalar() is long max)
                {
                    lastSeq = max;
                }
            }
        }

        return new ListenPage(listens, lastSeq, hasMore);
    }

    /// <summary>
    /// Returns the id of every user with stored listens (for the orphan prune).
    /// </summary>
    /// <returns>The user ids.</returns>
    public IReadOnlyList<Guid> GetAllUserIds()
    {
        var ids = new List<Guid>();
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT UserId FROM Listens";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
            }
        }

        return ids;
    }

    /// <summary>
    /// Forgets every listen of a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>How many listens were removed.</returns>
    public int DeleteUser(Guid userId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Listens WHERE UserId = $user";
            cmd.Parameters.AddWithValue("$user", FormatId(userId));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
    }

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();

        // AUTOINCREMENT, not a plain rowid: a deleted user's highest sequence must never be
        // handed out again, or a cursor past it would skip the listen that reused it.
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS Listens (
                Seq INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL,
                ListenId TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                PlayedAt INTEGER NOT NULL,
                DeviceId TEXT NOT NULL,
                ContextType TEXT NOT NULL,
                ContextId TEXT,
                UNIQUE (UserId, ListenId)
            );

            CREATE INDEX IF NOT EXISTS IX_Listens_User_Seq ON Listens (UserId, Seq);

            CREATE TABLE IF NOT EXISTS Metadata (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    private string LoadOrMintEpoch()
    {
        using (var insert = _connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO Metadata (Key, Value) VALUES ($key, $value)";
            insert.Parameters.AddWithValue("$key", EpochKey);
            insert.Parameters.AddWithValue("$value", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        using var select = _connection.CreateCommand();
        select.CommandText = "SELECT Value FROM Metadata WHERE Key = $key";
        select.Parameters.AddWithValue("$key", EpochKey);
        return (string)select.ExecuteScalar()!;
    }
}
