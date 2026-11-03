using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Persists computed <see cref="AudioInfoDto"/> records to SQLite, keyed by item id and
/// invalidated by source-file mtime plus a parser version. The values are stable per file, so a
/// cache hit lets the audio-info endpoint skip the ffprobe + header-parse round trip entirely.
/// </summary>
public class AudioInfoStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly object _connectionLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioInfoStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory path where the database file will be stored.</param>
    /// <param name="logger">Logger instance.</param>
    public AudioInfoStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "audioinfo.db");
        _logger = logger;

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        _logger.LogInformation("Audio info database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Returns the cached record for a track, but only when both the stored source mtime and the
    /// parser version match the supplied values. A mismatch (stale file or upgraded parser)
    /// returns <see langword="null"/> so the caller recomputes.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="sourceMtime">Current source file mtime in ticks.</param>
    /// <param name="version">Current parser version.</param>
    /// <returns>The cached DTO, or <see langword="null"/>.</returns>
    public AudioInfoDto? Get(Guid itemId, long sourceMtime, string version)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Codec, SampleRate, Channels, TotalFrames, TotalFramesExact,
                       EncoderDelay, EncoderPadding, MoovAtFront, DurationTicks
                FROM AudioInfo
                WHERE ItemId = $itemId AND SourceMtime = $sourceMtime AND Version = $version";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString());
            cmd.Parameters.AddWithValue("$sourceMtime", sourceMtime);
            cmd.Parameters.AddWithValue("$version", version);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AudioInfoDto
            {
                Codec = reader.GetString(0),
                SampleRate = reader.GetInt32(1),
                Channels = reader.GetInt32(2),
                TotalFrames = reader.GetInt64(3),
                TotalFramesExact = reader.GetInt64(4) != 0,
                EncoderDelay = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                EncoderPadding = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                MoovAtFront = reader.IsDBNull(7) ? null : reader.GetInt64(7) != 0,
                DurationTicks = reader.GetInt64(8)
            };
        }
    }

    /// <summary>
    /// Inserts or replaces the cached record for a track.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="info">The computed audio info.</param>
    /// <param name="sourceMtime">Current source file mtime in ticks.</param>
    /// <param name="version">Current parser version.</param>
    public void Upsert(Guid itemId, AudioInfoDto info, long sourceMtime, string version)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO AudioInfo (ItemId, Codec, SampleRate, Channels, TotalFrames, TotalFramesExact,
                                       EncoderDelay, EncoderPadding, MoovAtFront, DurationTicks, SourceMtime, Version)
                VALUES ($itemId, $codec, $sampleRate, $channels, $totalFrames, $totalFramesExact,
                        $encoderDelay, $encoderPadding, $moovAtFront, $durationTicks, $sourceMtime, $version)
                ON CONFLICT(ItemId) DO UPDATE SET
                    Codec = excluded.Codec,
                    SampleRate = excluded.SampleRate,
                    Channels = excluded.Channels,
                    TotalFrames = excluded.TotalFrames,
                    TotalFramesExact = excluded.TotalFramesExact,
                    EncoderDelay = excluded.EncoderDelay,
                    EncoderPadding = excluded.EncoderPadding,
                    MoovAtFront = excluded.MoovAtFront,
                    DurationTicks = excluded.DurationTicks,
                    SourceMtime = excluded.SourceMtime,
                    Version = excluded.Version";

            cmd.Parameters.AddWithValue("$itemId", itemId.ToString());
            cmd.Parameters.AddWithValue("$codec", info.Codec);
            cmd.Parameters.AddWithValue("$sampleRate", info.SampleRate);
            cmd.Parameters.AddWithValue("$channels", info.Channels);
            cmd.Parameters.AddWithValue("$totalFrames", info.TotalFrames);
            cmd.Parameters.AddWithValue("$totalFramesExact", info.TotalFramesExact ? 1 : 0);
            cmd.Parameters.AddWithValue("$encoderDelay", (object?)info.EncoderDelay ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$encoderPadding", (object?)info.EncoderPadding ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$moovAtFront", info.MoovAtFront == null ? DBNull.Value : info.MoovAtFront.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$durationTicks", info.DurationTicks);
            cmd.Parameters.AddWithValue("$sourceMtime", sourceMtime);
            cmd.Parameters.AddWithValue("$version", version);

            cmd.ExecuteNonQuery();
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
            cmd.CommandText = "SELECT DISTINCT ItemId FROM AudioInfo";

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
    /// Removes a single track's cached info.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    public void Delete(Guid itemId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM AudioInfo WHERE ItemId = $itemId";
            cmd.Parameters.AddWithValue("$itemId", itemId.ToString());
            cmd.ExecuteNonQuery();
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

            CREATE TABLE IF NOT EXISTS AudioInfo (
                ItemId TEXT PRIMARY KEY,
                Codec TEXT NOT NULL,
                SampleRate INTEGER NOT NULL,
                Channels INTEGER NOT NULL,
                TotalFrames INTEGER NOT NULL,
                TotalFramesExact INTEGER NOT NULL,
                EncoderDelay INTEGER,
                EncoderPadding INTEGER,
                MoovAtFront INTEGER,
                DurationTicks INTEGER NOT NULL,
                SourceMtime INTEGER NOT NULL,
                Version TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }
}
