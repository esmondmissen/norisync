using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Persists Lidarr request attribution to a SQLite database. One row per
/// (Jellyfin user, Lidarr album) pair; rows are removed when the album finishes importing
/// (Lidarr becomes the source of truth from that point on).
/// </summary>
public class RequestsStore : IDisposable
{
    private const string BaseSelect = @"
        SELECT JellyfinUserId, LidarrAlbumId, AlbumMbid, AlbumTitle, ArtistName,
               SubmittedAt, Status, StatusUpdatedAt, DownloadProgress, LastError
        FROM Requests";

    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly object _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestsStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory path where the database file will be stored.</param>
    /// <param name="logger">Logger instance.</param>
    public RequestsStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "requests.db");
        _logger = logger;

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        _logger.LogInformation("Lidarr requests database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Inserts or updates a tracked request.
    /// </summary>
    /// <param name="record">The request record.</param>
    public void Upsert(RequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Requests (
                    JellyfinUserId, LidarrAlbumId, AlbumMbid, AlbumTitle, ArtistName,
                    SubmittedAt, Status, StatusUpdatedAt, DownloadProgress, LastError)
                VALUES (
                    $userId, $albumId, $mbid, $title, $artist,
                    $submittedAt, $status, $statusAt, $progress, $error)
                ON CONFLICT(JellyfinUserId, LidarrAlbumId) DO UPDATE SET
                    AlbumMbid        = excluded.AlbumMbid,
                    AlbumTitle       = excluded.AlbumTitle,
                    ArtistName       = excluded.ArtistName,
                    Status           = excluded.Status,
                    StatusUpdatedAt  = excluded.StatusUpdatedAt,
                    DownloadProgress = excluded.DownloadProgress,
                    LastError        = excluded.LastError;";

            cmd.Parameters.AddWithValue("$userId", record.JellyfinUserId.ToString());
            cmd.Parameters.AddWithValue("$albumId", record.LidarrAlbumId);
            cmd.Parameters.AddWithValue("$mbid", (object?)record.AlbumMbid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", (object?)record.AlbumTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$artist", (object?)record.ArtistName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$submittedAt", record.SubmittedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$status", record.Status.ToString());
            cmd.Parameters.AddWithValue("$statusAt", record.StatusUpdatedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$progress", (object?)record.DownloadProgress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$error", (object?)record.LastError ?? DBNull.Value);

            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns all requests submitted by a specific Jellyfin user.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The matching rows, newest-submitted first.</returns>
    public IReadOnlyList<RequestRecord> ListByUser(Guid userId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = BaseSelect + " WHERE JellyfinUserId = $userId ORDER BY SubmittedAt DESC";
        cmd.Parameters.AddWithValue("$userId", userId.ToString());
        return ExecuteQuery(cmd);
    }

    /// <summary>
    /// Returns all tracked requests across every user.
    /// </summary>
    /// <returns>All rows, newest-submitted first.</returns>
    public IReadOnlyList<RequestRecord> ListAll()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = BaseSelect + " ORDER BY SubmittedAt DESC";
        return ExecuteQuery(cmd);
    }

    /// <summary>
    /// Returns the distinct set of Lidarr album ids currently being tracked. Used by the
    /// sync task to decide which albums to poll Lidarr for.
    /// </summary>
    /// <returns>Distinct Lidarr album ids.</returns>
    public IReadOnlyList<int> GetDistinctAlbumIds()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT LidarrAlbumId FROM Requests";

        var ids = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    /// <summary>
    /// Returns the Lidarr album ids tracked for a specific user. Used to filter the
    /// proxied <c>/queue</c> response for non-admins.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The matching album ids as a hash set for O(1) <c>Contains</c> lookups.</returns>
    public HashSet<int> GetAlbumIdsForUser(Guid userId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT LidarrAlbumId FROM Requests WHERE JellyfinUserId = $userId";
        cmd.Parameters.AddWithValue("$userId", userId.ToString());

        var ids = new HashSet<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    /// <summary>
    /// Updates the status of every row tracking a specific Lidarr album id. Used by the
    /// sync task — all users tracking the same album see the same status.
    /// </summary>
    /// <param name="albumId">The Lidarr album id.</param>
    /// <param name="status">The new status.</param>
    /// <param name="downloadProgress">Optional download progress (0..1).</param>
    /// <param name="lastError">Optional error message.</param>
    /// <param name="now">The current UTC time.</param>
    public void UpdateStatusByAlbumId(int albumId, RequestStatus status, double? downloadProgress, string? lastError, DateTimeOffset now)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE Requests
                SET Status = $status,
                    StatusUpdatedAt = $now,
                    DownloadProgress = $progress,
                    LastError = $error
                WHERE LidarrAlbumId = $albumId";

            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$progress", (object?)downloadProgress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$albumId", albumId);

            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes every row tracking a Lidarr album id. Used when an album finishes importing
    /// or is removed from Lidarr — Lidarr is the source of truth from that point on.
    /// </summary>
    /// <param name="albumId">The Lidarr album id.</param>
    /// <returns>The number of rows removed.</returns>
    public int DeleteByAlbumId(int albumId)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Requests WHERE LidarrAlbumId = $albumId";
            cmd.Parameters.AddWithValue("$albumId", albumId);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes a single user's tracking row for a given album. Used by the cancel endpoint.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="albumId">The Lidarr album id.</param>
    /// <returns><see langword="true"/> if a row was removed.</returns>
    public bool Delete(Guid userId, int albumId)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Requests WHERE JellyfinUserId = $userId AND LidarrAlbumId = $albumId";
            cmd.Parameters.AddWithValue("$userId", userId.ToString());
            cmd.Parameters.AddWithValue("$albumId", albumId);
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

            CREATE TABLE IF NOT EXISTS Requests (
                JellyfinUserId    TEXT     NOT NULL,
                LidarrAlbumId     INTEGER  NOT NULL,
                AlbumMbid         TEXT,
                AlbumTitle        TEXT,
                ArtistName        TEXT,
                SubmittedAt       INTEGER  NOT NULL,
                Status            TEXT     NOT NULL,
                StatusUpdatedAt   INTEGER  NOT NULL,
                DownloadProgress  REAL,
                LastError         TEXT,
                PRIMARY KEY (JellyfinUserId, LidarrAlbumId)
            );

            CREATE INDEX IF NOT EXISTS IX_Requests_AlbumId ON Requests(LidarrAlbumId);
            CREATE INDEX IF NOT EXISTS IX_Requests_Status  ON Requests(Status);";
        cmd.ExecuteNonQuery();
    }

    private static List<RequestRecord> ExecuteQuery(SqliteCommand cmd)
    {
        var rows = new List<RequestRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RequestRecord
            {
                JellyfinUserId = Guid.Parse(reader.GetString(0)),
                LidarrAlbumId = reader.GetInt32(1),
                AlbumMbid = reader.IsDBNull(2) ? null : reader.GetString(2),
                AlbumTitle = reader.IsDBNull(3) ? null : reader.GetString(3),
                ArtistName = reader.IsDBNull(4) ? null : reader.GetString(4),
                SubmittedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                Status = Enum.TryParse<RequestStatus>(reader.GetString(6), out var s) ? s : RequestStatus.Pending,
                StatusUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                DownloadProgress = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                LastError = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }

        return rows;
    }
}
