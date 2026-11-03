using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Persists registered client app instances to a SQLite database. Used to track which
/// (user, device) pairs are consuming the change log so it can be purged safely.
/// </summary>
public class AppInstanceStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly object _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AppInstanceStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory path where the database file will be stored.</param>
    /// <param name="logger">Logger instance.</param>
    public AppInstanceStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "instances.db");
        _logger = logger;

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        _logger.LogInformation("App instance database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Upserts an instance record, refreshing its <see cref="AppInstance.LastSeenAt"/> timestamp
    /// and any newly reported client metadata.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="deviceId">The Jellyfin device id.</param>
    /// <param name="deviceName">Optional device name reported by the client.</param>
    /// <param name="clientName">Optional client/app name reported by the client.</param>
    /// <param name="now">The current UTC time.</param>
    public void Touch(Guid userId, string deviceId, string? deviceName, string? clientName, DateTimeOffset now)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Instances (UserId, DeviceId, DeviceName, ClientName, RegisteredAt, LastSeenAt, LastFullSyncAck)
                VALUES ($userId, $deviceId, $deviceName, $clientName, $now, $now, NULL)
                ON CONFLICT(UserId, DeviceId) DO UPDATE SET
                    LastSeenAt = excluded.LastSeenAt,
                    DeviceName = COALESCE(excluded.DeviceName, Instances.DeviceName),
                    ClientName = COALESCE(excluded.ClientName, Instances.ClientName);";

            cmd.Parameters.AddWithValue("$userId", userId.ToString());
            cmd.Parameters.AddWithValue("$deviceId", deviceId);
            cmd.Parameters.AddWithValue("$deviceName", (object?)deviceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clientName", (object?)clientName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());

            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records that a client has fully reconciled the change log up to <paramref name="ackTimestamp"/>.
    /// The entry is created if one did not already exist.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="deviceId">The Jellyfin device id.</param>
    /// <param name="ackTimestamp">The server timestamp the client has reconciled up to.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns><see langword="true"/> if a row was updated or inserted.</returns>
    public bool RecordAck(Guid userId, string deviceId, DateTimeOffset ackTimestamp, DateTimeOffset now)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Instances (UserId, DeviceId, RegisteredAt, LastSeenAt, LastFullSyncAck)
                VALUES ($userId, $deviceId, $now, $now, $ack)
                ON CONFLICT(UserId, DeviceId) DO UPDATE SET
                    LastSeenAt = excluded.LastSeenAt,
                    LastFullSyncAck = MAX(COALESCE(Instances.LastFullSyncAck, 0), excluded.LastFullSyncAck);";

            cmd.Parameters.AddWithValue("$userId", userId.ToString());
            cmd.Parameters.AddWithValue("$deviceId", deviceId);
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$ack", ackTimestamp.ToUnixTimeMilliseconds());

            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Returns all registered instances.
    /// </summary>
    /// <returns>All instances.</returns>
    public IReadOnlyList<AppInstance> GetAll()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT UserId, DeviceId, DeviceName, ClientName, RegisteredAt, LastSeenAt, LastFullSyncAck FROM Instances ORDER BY LastSeenAt DESC";
        return ExecuteQuery(cmd);
    }

    /// <summary>
    /// Returns instances that have been seen at or after <paramref name="since"/>.
    /// </summary>
    /// <param name="since">Only instances with <see cref="AppInstance.LastSeenAt"/> at or after this time are returned.</param>
    /// <returns>The matching instances.</returns>
    public IReadOnlyList<AppInstance> GetActiveSince(DateTimeOffset since)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT UserId, DeviceId, DeviceName, ClientName, RegisteredAt, LastSeenAt, LastFullSyncAck FROM Instances WHERE LastSeenAt >= $since ORDER BY LastSeenAt DESC";
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        return ExecuteQuery(cmd);
    }

    /// <summary>
    /// Removes instances that have not been seen since <paramref name="cutoff"/>.
    /// </summary>
    /// <param name="cutoff">Instances last seen before this time are removed.</param>
    /// <returns>The number of rows removed.</returns>
    public int RemoveIdleBefore(DateTimeOffset cutoff)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Instances WHERE LastSeenAt < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
            var deleted = cmd.ExecuteNonQuery();

            if (deleted > 0)
            {
                _logger.LogInformation("Removed {Count} idle app instances", deleted);
            }

            return deleted;
        }
    }

    /// <summary>
    /// Removes a single registered instance.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="deviceId">The Jellyfin device id.</param>
    /// <returns><see langword="true"/> if a row was removed.</returns>
    public bool Remove(Guid userId, string deviceId)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Instances WHERE UserId = $userId AND DeviceId = $deviceId";
            cmd.Parameters.AddWithValue("$userId", userId.ToString());
            cmd.Parameters.AddWithValue("$deviceId", deviceId);
            return cmd.ExecuteNonQuery() > 0;
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

            CREATE TABLE IF NOT EXISTS Instances (
                UserId TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                DeviceName TEXT,
                ClientName TEXT,
                RegisteredAt INTEGER NOT NULL,
                LastSeenAt INTEGER NOT NULL,
                LastFullSyncAck INTEGER,
                PRIMARY KEY (UserId, DeviceId)
            );

            CREATE INDEX IF NOT EXISTS IX_Instances_LastSeenAt ON Instances(LastSeenAt);
            CREATE INDEX IF NOT EXISTS IX_Instances_LastFullSyncAck ON Instances(LastFullSyncAck);";
        cmd.ExecuteNonQuery();
    }

    private static List<AppInstance> ExecuteQuery(SqliteCommand cmd)
    {
        var instances = new List<AppInstance>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            instances.Add(new AppInstance
            {
                UserId = Guid.Parse(reader.GetString(0)),
                DeviceId = reader.GetString(1),
                DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                ClientName = reader.IsDBNull(3) ? null : reader.GetString(3),
                RegisteredAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                LastSeenAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                LastFullSyncAck = reader.IsDBNull(6)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))
            });
        }

        return instances;
    }
}
