using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Persists the editable source of playlist covers made in Nori's cover designer, so every
/// device (and every user who can see the playlist) can draw the designed page instead of only
/// the baked image. One row per playlist: the design document as the client sent it, plus the
/// identity of the primary image it was baked into.
/// </summary>
/// <remarks>
/// A design is only true while the playlist still shows the image it was uploaded with. The
/// cover can change behind the plugin's back (the Jellyfin web UI, another client, an image
/// refresh), so a row is never trusted on its own: <see cref="Describes"/> compares the stored
/// image identity with the playlist's current primary image, and a row that no longer matches
/// is simply not served. The document itself is opaque here — the client validates what it
/// draws.
/// </remarks>
public sealed class PlaylistCoverDesignStore : IDisposable
{
    /// <summary>
    /// The largest design document accepted, in UTF-16 characters. Real designs are 1–2 KB; the
    /// cap only stops the table being used as general storage.
    /// </summary>
    public const int MaxDesignLength = 32 * 1024;

    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly object _connectionLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistCoverDesignStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory path where the database file will be stored.</param>
    /// <param name="logger">Logger instance.</param>
    public PlaylistCoverDesignStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "coverdesigns.db");
        _logger = logger;

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        _logger.LogInformation("Playlist cover design database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Whether <paramref name="record"/> describes the image at <paramref name="imagePath"/> last
    /// modified at <paramref name="imageDateModified"/> — i.e. the playlist still shows the
    /// cover the design was baked into.
    /// </summary>
    /// <param name="record">The stored design.</param>
    /// <param name="imagePath">The playlist's current primary image path, if it has one.</param>
    /// <param name="imageDateModified">That image's modification time.</param>
    /// <returns><see langword="true"/> when the stored image identity matches.</returns>
    public static bool Describes(PlaylistCoverDesignRecord record, string? imagePath, DateTime imageDateModified)
    {
        ArgumentNullException.ThrowIfNull(record);
        return imagePath != null
            && string.Equals(record.ImagePath, imagePath, StringComparison.Ordinal)
            && record.ImageDateModifiedTicks == imageDateModified.Ticks;
    }

    /// <summary>
    /// Stores the design for a playlist, replacing any earlier one.
    /// </summary>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="design">The design document (JSON) as the client sent it.</param>
    /// <param name="imagePath">The path of the primary image the design was baked into.</param>
    /// <param name="imageDateModified">That image's modification time.</param>
    /// <param name="now">The current UTC time.</param>
    public void Save(Guid playlistId, string design, string imagePath, DateTime imageDateModified, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(imagePath);

        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO PlaylistCoverDesigns (PlaylistId, Design, ImagePath, ImageDateModified, UpdatedAt)
                VALUES ($id, $design, $path, $modified, $updated)
                ON CONFLICT(PlaylistId) DO UPDATE SET
                    Design = excluded.Design,
                    ImagePath = excluded.ImagePath,
                    ImageDateModified = excluded.ImageDateModified,
                    UpdatedAt = excluded.UpdatedAt;";
            cmd.Parameters.AddWithValue("$id", FormatId(playlistId));
            cmd.Parameters.AddWithValue("$design", design);
            cmd.Parameters.AddWithValue("$path", imagePath);
            cmd.Parameters.AddWithValue("$modified", imageDateModified.Ticks);
            cmd.Parameters.AddWithValue("$updated", now.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns the stored design for a playlist, whether or not it still matches its cover.
    /// </summary>
    /// <param name="playlistId">The playlist id.</param>
    /// <returns>The record, or <see langword="null"/> if none is stored.</returns>
    public PlaylistCoverDesignRecord? Get(Guid playlistId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT PlaylistId, Design, ImagePath, ImageDateModified FROM PlaylistCoverDesigns WHERE PlaylistId = $id";
            cmd.Parameters.AddWithValue("$id", FormatId(playlistId));
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }
    }

    /// <summary>
    /// Returns every stored design. Playlists are few, so callers filter in memory.
    /// </summary>
    /// <returns>All stored records.</returns>
    public IReadOnlyList<PlaylistCoverDesignRecord> GetAll()
    {
        var records = new List<PlaylistCoverDesignRecord>();
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT PlaylistId, Design, ImagePath, ImageDateModified FROM PlaylistCoverDesigns ORDER BY PlaylistId";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                records.Add(ReadRecord(reader));
            }
        }

        return records;
    }

    /// <summary>
    /// Returns the ids of every playlist with a stored design (for the orphan prune).
    /// </summary>
    /// <returns>The stored playlist ids.</returns>
    public IReadOnlyList<Guid> GetAllItemIds()
    {
        var ids = new List<Guid>();
        foreach (var record in GetAll())
        {
            ids.Add(record.PlaylistId);
        }

        return ids;
    }

    /// <summary>
    /// Forgets a playlist's design.
    /// </summary>
    /// <param name="playlistId">The playlist id.</param>
    /// <returns><see langword="true"/> if a row was removed.</returns>
    public bool Delete(Guid playlistId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM PlaylistCoverDesigns WHERE PlaylistId = $id";
            cmd.Parameters.AddWithValue("$id", FormatId(playlistId));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
    }

    private static PlaylistCoverDesignRecord ReadRecord(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3));

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS PlaylistCoverDesigns (
                PlaylistId TEXT NOT NULL PRIMARY KEY,
                Design TEXT NOT NULL,
                ImagePath TEXT NOT NULL,
                ImageDateModified INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }
}
