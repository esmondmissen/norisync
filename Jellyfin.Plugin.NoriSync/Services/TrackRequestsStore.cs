using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Song requests (<c>track-requests.db</c>): one row per (Jellyfin user, recording). Users asking
/// for the same recording share SoulSync's one wishlist row, so status is written per recording.
/// A row is removed once the song is in the Jellyfin library.
/// </summary>
public sealed class TrackRequestsStore : IDisposable
{
    private const string Select = @"
        SELECT JellyfinUserId, RecordingMbid, SoulSyncTrackId, Title, ArtistName, AlbumTitle,
               ReleaseGroupMbid, SubmittedAt, Status, StatusUpdatedAt, DownloadProgress, LastError,
               RetryCount, LastAttemptAt, WishlistMissingSince
        FROM TrackRequests";

    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackRequestsStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory the database file lives in.</param>
    /// <param name="logger">Logger instance.</param>
    public TrackRequestsStore(string dataPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "track-requests.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS TrackRequests (
                JellyfinUserId        TEXT     NOT NULL,
                RecordingMbid         TEXT     NOT NULL,
                SoulSyncTrackId       TEXT,
                Title                 TEXT,
                ArtistName            TEXT,
                AlbumTitle            TEXT,
                ReleaseGroupMbid      TEXT,
                SubmittedAt           INTEGER  NOT NULL,
                Status                TEXT     NOT NULL,
                StatusUpdatedAt       INTEGER  NOT NULL,
                DownloadProgress      REAL,
                LastError             TEXT,
                RetryCount            INTEGER,
                LastAttemptAt         INTEGER,
                WishlistMissingSince  INTEGER,
                PRIMARY KEY (JellyfinUserId, RecordingMbid)
            );

            CREATE INDEX IF NOT EXISTS IX_TrackRequests_Recording ON TrackRequests(RecordingMbid);";
        cmd.ExecuteNonQuery();
        logger.LogInformation("Song requests database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Inserts or replaces a user's request for a recording.
    /// </summary>
    /// <param name="record">The request.</param>
    public void Upsert(TrackRequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO TrackRequests (
                    JellyfinUserId, RecordingMbid, SoulSyncTrackId, Title, ArtistName, AlbumTitle,
                    ReleaseGroupMbid, SubmittedAt, Status, StatusUpdatedAt, DownloadProgress, LastError,
                    RetryCount, LastAttemptAt, WishlistMissingSince)
                VALUES (
                    $user, $mbid, $trackId, $title, $artist, $album,
                    $rg, $submitted, $status, $statusAt, $progress, $error,
                    $retries, $attempt, $missing)";
            cmd.Parameters.AddWithValue("$user", record.JellyfinUserId.ToString());
            cmd.Parameters.AddWithValue("$mbid", Key(record.RecordingMbid));
            cmd.Parameters.AddWithValue("$trackId", (object?)record.SoulSyncTrackId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", (object?)record.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$artist", (object?)record.ArtistName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$album", (object?)record.AlbumTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rg", (object?)record.ReleaseGroupMbid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$submitted", record.SubmittedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$status", record.Status.ToString());
            cmd.Parameters.AddWithValue("$statusAt", record.StatusUpdatedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$progress", (object?)record.DownloadProgress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$error", (object?)record.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$retries", (object?)record.RetryCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$attempt", (object?)record.LastAttemptAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$missing", (object?)record.WishlistMissingSince?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// A user's request for a recording, or null.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="recordingMbid">The recording.</param>
    /// <returns>The request, or null.</returns>
    public TrackRequestRecord? Get(Guid userId, string recordingMbid)
    {
        var rows = Query(" WHERE JellyfinUserId = $user AND RecordingMbid = $mbid", ("$user", userId.ToString()), ("$mbid", Key(recordingMbid)));
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// A user's requests, newest first.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The requests.</returns>
    public IReadOnlyList<TrackRequestRecord> ListByUser(Guid userId)
        => Query(" WHERE JellyfinUserId = $user ORDER BY SubmittedAt DESC", ("$user", userId.ToString()));

    /// <summary>
    /// Every user's requests, newest first.
    /// </summary>
    /// <returns>The requests.</returns>
    public IReadOnlyList<TrackRequestRecord> ListAll() => Query(" ORDER BY SubmittedAt DESC");

    /// <summary>
    /// Every request for a recording.
    /// </summary>
    /// <param name="recordingMbid">The recording.</param>
    /// <returns>Its requests.</returns>
    public IReadOnlyList<TrackRequestRecord> ListByRecording(string recordingMbid)
        => Query(" WHERE RecordingMbid = $mbid", ("$mbid", Key(recordingMbid)));

    /// <summary>
    /// Writes where a recording stands onto every request for it.
    /// </summary>
    /// <param name="recordingMbid">The recording.</param>
    /// <param name="status">The status.</param>
    /// <param name="progress">Download progress, 0..1.</param>
    /// <param name="error">SoulSync's last failure reason.</param>
    /// <param name="retryCount">How many tries SoulSync has made.</param>
    /// <param name="lastAttemptAt">When it last tried.</param>
    /// <param name="wishlistMissingSince">Since when it has been off the wishlist, or null.</param>
    /// <param name="now">The current time.</param>
    public void UpdateStatus(
        string recordingMbid,
        TrackRequestStatus status,
        double? progress,
        string? error,
        int? retryCount,
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset? wishlistMissingSince,
        DateTimeOffset now)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE TrackRequests SET
                    Status = $status, StatusUpdatedAt = $now, DownloadProgress = $progress,
                    LastError = $error, RetryCount = $retries, LastAttemptAt = $attempt,
                    WishlistMissingSince = $missing
                WHERE RecordingMbid = $mbid";
            cmd.Parameters.AddWithValue("$mbid", Key(recordingMbid));
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$progress", (object?)progress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$retries", (object?)retryCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$attempt", (object?)lastAttemptAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$missing", (object?)wishlistMissingSince?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes every request for a recording.
    /// </summary>
    /// <param name="recordingMbid">The recording.</param>
    /// <returns>How many rows went.</returns>
    public int DeleteByRecording(string recordingMbid) => Execute("DELETE FROM TrackRequests WHERE RecordingMbid = $mbid", ("$mbid", Key(recordingMbid)));

    /// <summary>
    /// Removes one user's request.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="recordingMbid">The recording.</param>
    /// <returns>True when a row went.</returns>
    public bool Delete(Guid userId, string recordingMbid)
        => Execute("DELETE FROM TrackRequests WHERE JellyfinUserId = $user AND RecordingMbid = $mbid", ("$user", userId.ToString()), ("$mbid", Key(recordingMbid))) > 0;

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();

    // MusicBrainz ids are lower-case; an id typed or sent upper-case is the same recording.
    private static string Key(string recordingMbid) => recordingMbid.Trim().ToLowerInvariant();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers pass constant SQL; values go in as parameters.")]
    private int Execute(string sql, params (string Name, string Value)[] parameters)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            return cmd.ExecuteNonQuery();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers pass constant SQL; values go in as parameters.")]
    private List<TrackRequestRecord> Query(string where, params (string Name, string Value)[] parameters)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = Select + where;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            var rows = new List<TrackRequestRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new TrackRequestRecord
                {
                    JellyfinUserId = Guid.Parse(reader.GetString(0)),
                    RecordingMbid = reader.GetString(1),
                    SoulSyncTrackId = Text(reader, 2),
                    Title = Text(reader, 3),
                    ArtistName = Text(reader, 4),
                    AlbumTitle = Text(reader, 5),
                    ReleaseGroupMbid = Text(reader, 6),
                    SubmittedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                    Status = Enum.TryParse<TrackRequestStatus>(reader.GetString(8), out var status) ? status : TrackRequestStatus.Queued,
                    StatusUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                    DownloadProgress = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    LastError = Text(reader, 11),
                    RetryCount = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    LastAttemptAt = Time(reader, 13),
                    WishlistMissingSince = Time(reader, 14),
                });
            }

            return rows;
        }
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? Time(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));
}
