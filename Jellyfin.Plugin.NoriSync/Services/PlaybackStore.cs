using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Each of a user's devices as the server holds it — its session, where it is in it, what it is
/// doing — and the queues those sessions name (API_FORMAT §3.20). Times are the server's clock:
/// no device's clock is stored. A queue is immutable under its (session, revision): a new one is
/// a new revision.
/// </summary>
public sealed class PlaybackStore : IDisposable
{
    /// <summary>
    /// The most devices kept per user; the least recently heard from go first.
    /// </summary>
    public const int MaxDevicesPerUser = 16;

    /// <summary>
    /// A device not heard from for this long is forgotten at the user's next report.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// A report number this far BELOW the last applied is a device that started counting again
    /// (its clock moved back, or it was reinstalled), not an old report: it is applied.
    /// </summary>
    public static readonly TimeSpan SeqResetWindow = TimeSpan.FromDays(1);

    private const string DeviceColumns = @"
        DeviceId, DeviceName, SessionId, QueueRev, Shareable, EntryIndex, ItemId, PositionTicks,
        PositionAt, Status, LastSeq, Version, UpdatedAt, MovedTo, MovedToName, MovedSessionId";

    private readonly SqliteConnection _connection;
    private readonly object _connectionLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory the database file is kept in.</param>
    /// <param name="logger">Logger.</param>
    public PlaybackStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "playback.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeDatabase();
        logger.LogInformation("Playback database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Applies one report of <paramref name="deviceId"/>'s, in one transaction: the report number
    /// check, the queue it carries, a take-over, and the device's record.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The reporting device.</param>
    /// <param name="deviceName">Its name, if it gave one.</param>
    /// <param name="report">The report.</param>
    /// <param name="now">The server's clock.</param>
    /// <returns>What it did.</returns>
    public PlaybackApplyOutcome ApplyReport(Guid userId, string deviceId, string? deviceName, PlaybackReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(report);
        var nowMs = now.ToUnixTimeMilliseconds();
        var user = FormatId(userId);
        lock (_connectionLock)
        {
            using var transaction = _connection.BeginTransaction();
            var existing = ReadDevice(transaction, user, deviceId);
            if (existing != null && IsStale(report.Seq, existing.LastSeq))
            {
                return PlaybackApplyOutcome.StaleReport;
            }

            var version = NextVersion(transaction, user, nowMs);
            var changed = new List<PlaybackDevice>();

            // A device reporting the session it lost keeps no session: the device that took it holds
            // it. Its status and position still update, so it reads as what it is doing. Taking it
            // back (a take-over of it) is not that.
            var moved = existing?.MovedSessionId != null
                && string.Equals(existing.MovedSessionId, report.SessionId, StringComparison.Ordinal)
                && report.TakeOverSessionId == null;

            // Not from a device whose session moved: the revision it names is the new holder's.
            if (!moved && report.Queue != null && report.Shareable)
            {
                Execute(
                    transaction,
                    @"INSERT OR IGNORE INTO PlaybackQueues (UserId, SessionId, QueueRev, Queue, CreatedAt)
                      VALUES ($user, $session, $rev, $queue, $now)",
                    ("$user", user),
                    ("$session", report.SessionId),
                    ("$rev", report.QueueRev),
                    ("$queue", report.Queue),
                    ("$now", nowMs));
            }

            string? tookOverFrom = null;
            if (!moved && report.TakeOverSessionId != null)
            {
                var holder = FindHolder(transaction, user, deviceId, report.TakeOverSessionId);
                if (holder != null)
                {
                    var source = holder with
                    {
                        SessionId = null,
                        QueueRev = null,
                        Status = holder.Status == PlaybackStatus.Playing ? PlaybackStatus.Paused : holder.Status,
                        Version = version,
                        MovedTo = deviceId,
                        MovedToName = deviceName,
                        MovedSessionId = report.TakeOverSessionId
                    };
                    WriteDevice(transaction, user, source);
                    changed.Add(source);
                    tookOverFrom = holder.DeviceId;
                }
            }

            var record = new PlaybackDevice(
                userId,
                existing?.DeviceId ?? deviceId,
                deviceName ?? existing?.DeviceName,
                moved ? null : report.SessionId,
                moved ? null : report.QueueRev,
                report.Shareable,
                report.EntryIndex,
                report.ItemId.ToString("N", CultureInfo.InvariantCulture),
                report.PositionTicks,
                nowMs,
                report.Status,
                report.Seq,
                version,
                nowMs,
                moved ? existing!.MovedTo : null,
                moved ? existing!.MovedToName : null,
                moved ? existing!.MovedSessionId : null);
            WriteDevice(transaction, user, record);
            changed.Insert(0, record);

            var queueNeeded = !moved && report.Shareable && !HasQueue(transaction, user, report.SessionId, report.QueueRev);
            Prune(transaction, user, nowMs);
            transaction.Commit();
            return new PlaybackApplyOutcome(false, queueNeeded, moved ? record : null, tookOverFrom, changed);
        }
    }

    /// <summary>
    /// Clears <paramref name="deviceId"/>'s session if it is still <paramref name="sessionId"/>: the
    /// device stopped it. A session it no longer holds (moved away, or replaced) is left alone.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="sessionId">The session it stopped.</param>
    /// <param name="now">The server's clock.</param>
    /// <returns>The changed record, or <see langword="null"/> when nothing changed.</returns>
    public PlaybackDevice? ClearSession(Guid userId, string deviceId, string sessionId, DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var user = FormatId(userId);
        lock (_connectionLock)
        {
            using var transaction = _connection.BeginTransaction();
            var existing = ReadDevice(transaction, user, deviceId);
            if (existing == null || !string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal))
            {
                return null;
            }

            var cleared = existing with
            {
                SessionId = null,
                QueueRev = null,
                Status = PlaybackStatus.Stopped,
                Version = NextVersion(transaction, user, nowMs),
                UpdatedAt = nowMs
            };
            WriteDevice(transaction, user, cleared);
            Prune(transaction, user, nowMs);
            transaction.Commit();
            return cleared;
        }
    }

    /// <summary>
    /// Returns every device of the user.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The devices.</returns>
    public IReadOnlyList<PlaybackDevice> GetDevices(Guid userId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT {DeviceColumns} FROM PlaybackDevices WHERE UserId = $user";
            cmd.Parameters.AddWithValue("$user", FormatId(userId));
            return ReadDevices(cmd, userId);
        }
    }

    /// <summary>
    /// Returns one device of the user.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <returns>The device, or <see langword="null"/> if the server holds none by that id.</returns>
    public PlaybackDevice? GetDevice(Guid userId, string deviceId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT {DeviceColumns} FROM PlaybackDevices WHERE UserId = $user AND DeviceId = $device";
            cmd.Parameters.AddWithValue("$user", FormatId(userId));
            cmd.Parameters.AddWithValue("$device", deviceId);
            var devices = ReadDevices(cmd, userId);
            return devices.Count > 0 ? devices[0] : null;
        }
    }

    /// <summary>
    /// Returns every device, of every user, whose latest report says playing since
    /// <paramref name="since"/>: the devices whose liveness can change with no report.
    /// </summary>
    /// <param name="since">The oldest report counted.</param>
    /// <returns>The devices.</returns>
    public IReadOnlyList<PlaybackDevice> GetPlayingDevices(DateTimeOffset since)
    {
        var devices = new List<PlaybackDevice>();
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT UserId, {DeviceColumns} FROM PlaybackDevices WHERE Status = $playing AND PositionAt >= $since";
            cmd.Parameters.AddWithValue("$playing", (int)PlaybackStatus.Playing);
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                devices.Add(ReadDevice(reader, Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture), 1));
            }
        }

        return devices;
    }

    /// <summary>
    /// Returns the queue a session holds at a revision.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="queueRev">The revision.</param>
    /// <returns>The queue's JSON as the device sent it, or <see langword="null"/>.</returns>
    public string? GetQueue(Guid userId, string sessionId, int queueRev)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Queue FROM PlaybackQueues WHERE UserId = $user AND SessionId = $session AND QueueRev = $rev";
            cmd.Parameters.AddWithValue("$user", FormatId(userId));
            cmd.Parameters.AddWithValue("$session", sessionId);
            cmd.Parameters.AddWithValue("$rev", queueRev);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>
    /// Forgets every device and queue of a user (the user was deleted).
    /// </summary>
    /// <param name="userId">The user.</param>
    public void DeleteUser(Guid userId)
    {
        lock (_connectionLock)
        {
            using var transaction = _connection.BeginTransaction();
            Execute(transaction, "DELETE FROM PlaybackDevices WHERE UserId = $user", ("$user", FormatId(userId)));
            Execute(transaction, "DELETE FROM PlaybackQueues WHERE UserId = $user", ("$user", FormatId(userId)));
            transaction.Commit();
        }
    }

    /// <summary>
    /// Returns the id of every user the store holds anything for (for the orphan prune).
    /// </summary>
    /// <returns>The user ids.</returns>
    public IReadOnlyList<Guid> GetAllUserIds()
    {
        var ids = new List<Guid>();
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT UserId FROM PlaybackDevices UNION SELECT UserId FROM PlaybackQueues";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
            }
        }

        return ids;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <summary>
    /// Whether a report numbered <paramref name="seq"/> comes after <paramref name="lastSeq"/>.
    /// </summary>
    /// <param name="seq">The report's number.</param>
    /// <param name="lastSeq">The device's last applied.</param>
    /// <returns><see langword="true"/> if the report is to be dropped.</returns>
    internal static bool IsStale(long seq, long lastSeq) =>
        seq <= lastSeq && lastSeq - seq <= (long)SeqResetWindow.TotalMilliseconds;

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static List<PlaybackDevice> ReadDevices(SqliteCommand cmd, Guid userId)
    {
        var devices = new List<PlaybackDevice>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            devices.Add(ReadDevice(reader, userId, 0));
        }

        return devices;
    }

    private static PlaybackDevice ReadDevice(SqliteDataReader reader, Guid userId, int first)
    {
        string? Text(int i) => reader.IsDBNull(first + i) ? null : reader.GetString(first + i);
        return new PlaybackDevice(
            userId,
            reader.GetString(first),
            Text(1),
            Text(2),
            reader.IsDBNull(first + 3) ? null : reader.GetInt32(first + 3),
            reader.GetInt64(first + 4) != 0,
            reader.GetInt32(first + 5),
            Text(6),
            reader.GetInt64(first + 7),
            reader.GetInt64(first + 8),
            (PlaybackStatus)reader.GetInt32(first + 9),
            reader.GetInt64(first + 10),
            reader.GetInt64(first + 11),
            reader.GetInt64(first + 12),
            Text(13),
            Text(14),
            Text(15));
    }

    private PlaybackDevice? ReadDevice(SqliteTransaction transaction, string user, string deviceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT {DeviceColumns} FROM PlaybackDevices WHERE UserId = $user AND DeviceId = $device";
        cmd.Parameters.AddWithValue("$user", user);
        cmd.Parameters.AddWithValue("$device", deviceId);
        var devices = ReadDevices(cmd, Guid.Parse(user, CultureInfo.InvariantCulture));
        return devices.Count > 0 ? devices[0] : null;
    }

    private PlaybackDevice? FindHolder(SqliteTransaction transaction, string user, string notDevice, string sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $@"SELECT {DeviceColumns} FROM PlaybackDevices
            WHERE UserId = $user AND SessionId = $session AND DeviceId <> $device
            ORDER BY UpdatedAt DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$user", user);
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$device", notDevice);
        var devices = ReadDevices(cmd, Guid.Parse(user, CultureInfo.InvariantCulture));
        return devices.Count > 0 ? devices[0] : null;
    }

    private void WriteDevice(SqliteTransaction transaction, string user, PlaybackDevice device) =>
        Execute(
            transaction,
            @"INSERT INTO PlaybackDevices (UserId, DeviceId, DeviceName, SessionId, QueueRev, Shareable, EntryIndex,
                  ItemId, PositionTicks, PositionAt, Status, LastSeq, Version, UpdatedAt, MovedTo, MovedToName, MovedSessionId)
              VALUES ($user, $device, $name, $session, $rev, $shareable, $entry, $item, $ticks, $at, $status, $seq,
                  $version, $updated, $movedTo, $movedToName, $movedSession)
              ON CONFLICT(UserId, DeviceId) DO UPDATE SET
                  DeviceName = excluded.DeviceName, SessionId = excluded.SessionId, QueueRev = excluded.QueueRev,
                  Shareable = excluded.Shareable, EntryIndex = excluded.EntryIndex, ItemId = excluded.ItemId,
                  PositionTicks = excluded.PositionTicks, PositionAt = excluded.PositionAt, Status = excluded.Status,
                  LastSeq = excluded.LastSeq, Version = excluded.Version, UpdatedAt = excluded.UpdatedAt,
                  MovedTo = excluded.MovedTo, MovedToName = excluded.MovedToName, MovedSessionId = excluded.MovedSessionId",
            ("$user", user),
            ("$device", device.DeviceId),
            ("$name", device.DeviceName),
            ("$session", device.SessionId),
            ("$rev", device.QueueRev),
            ("$shareable", device.Shareable ? 1 : 0),
            ("$entry", device.EntryIndex),
            ("$item", device.ItemId),
            ("$ticks", device.PositionTicks),
            ("$at", device.PositionAt),
            ("$status", (int)device.Status),
            ("$seq", device.LastSeq),
            ("$version", device.Version),
            ("$updated", device.UpdatedAt),
            ("$movedTo", device.MovedTo),
            ("$movedToName", device.MovedToName),
            ("$movedSession", device.MovedSessionId));

    /// <summary>
    /// Above every version the user's devices hold, and never below the clock: a database that was
    /// lost and started again still counts up from what clients last saw.
    /// </summary>
    private long NextVersion(SqliteTransaction transaction, string user, long nowMs)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT MAX(Version) FROM PlaybackDevices WHERE UserId = $user";
        cmd.Parameters.AddWithValue("$user", user);
        var highest = cmd.ExecuteScalar() is long value ? value : 0;
        return Math.Max(highest + 1, nowMs);
    }

    private bool HasQueue(SqliteTransaction transaction, string user, string sessionId, int queueRev)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT 1 FROM PlaybackQueues WHERE UserId = $user AND SessionId = $session AND QueueRev = $rev";
        cmd.Parameters.AddWithValue("$user", user);
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$rev", queueRev);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// Devices unheard for <see cref="StaleAfter"/> and past <see cref="MaxDevicesPerUser"/>, then
    /// every queue no device's record names.
    /// </summary>
    private void Prune(SqliteTransaction transaction, string user, long nowMs) =>
        Execute(
            transaction,
            @"DELETE FROM PlaybackDevices WHERE UserId = $user AND UpdatedAt < $staleBefore;
              DELETE FROM PlaybackDevices WHERE UserId = $user AND DeviceId NOT IN (
                  SELECT DeviceId FROM PlaybackDevices WHERE UserId = $user ORDER BY UpdatedAt DESC LIMIT $max);
              DELETE FROM PlaybackQueues WHERE UserId = $user AND NOT EXISTS (
                  SELECT 1 FROM PlaybackDevices d WHERE d.UserId = PlaybackQueues.UserId
                      AND d.SessionId = PlaybackQueues.SessionId AND d.QueueRev = PlaybackQueues.QueueRev);",
            ("$user", user),
            ("$staleBefore", nowMs - (long)StaleAfter.TotalMilliseconds),
            ("$max", MaxDevicesPerUser));

    private void Execute(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
#pragma warning disable CA2100 // Literal SQL; every value is a parameter.
        cmd.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS PlaybackDevices (
                UserId TEXT NOT NULL,
                DeviceId TEXT NOT NULL COLLATE NOCASE,
                DeviceName TEXT,
                SessionId TEXT,
                QueueRev INTEGER,
                Shareable INTEGER NOT NULL,
                EntryIndex INTEGER NOT NULL,
                ItemId TEXT,
                PositionTicks INTEGER NOT NULL,
                PositionAt INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                LastSeq INTEGER NOT NULL,
                Version INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                MovedTo TEXT,
                MovedToName TEXT,
                MovedSessionId TEXT,
                PRIMARY KEY (UserId, DeviceId)
            );

            CREATE TABLE IF NOT EXISTS PlaybackQueues (
                UserId TEXT NOT NULL,
                SessionId TEXT NOT NULL,
                QueueRev INTEGER NOT NULL,
                Queue TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                PRIMARY KEY (UserId, SessionId, QueueRev)
            );";
        cmd.ExecuteNonQuery();
    }
}
