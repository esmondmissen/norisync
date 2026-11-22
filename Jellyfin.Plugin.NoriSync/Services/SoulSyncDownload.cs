using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// One of SoulSync's download tasks (<c>GET downloads</c>). Tasks carry no wishlist id: they are
/// matched to requests by title and artist (<see cref="TrackMatch"/>).
/// </summary>
/// <param name="Status">SoulSync's status (<c>queued</c>, <c>searching</c>, <c>downloading</c>, <c>post_processing</c>, <c>completed</c>, <c>failed</c>, <c>not_found</c>, <c>cancelled</c>…).</param>
/// <param name="Title">The track name.</param>
/// <param name="Artist">The artist name.</param>
/// <param name="Album">The album name.</param>
/// <param name="Progress">Progress, 0..1.</param>
/// <param name="Error">The task's error.</param>
public sealed record SoulSyncDownload(string? Status, string? Title, string? Artist, string? Album, double Progress, string? Error)
{
    /// <summary>
    /// Reads a <c>GET downloads</c> reply's tasks, newest first as SoulSync lists them.
    /// </summary>
    /// <param name="data">The envelope's <c>data</c>.</param>
    /// <returns>The tasks.</returns>
    internal static List<SoulSyncDownload> Read(JsonElement? data)
    {
        var tasks = new List<SoulSyncDownload>();
        if (data is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("downloads", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return tasks;
        }

        foreach (var task in list.EnumerateArray())
        {
            var progress = task.TryGetProperty("progress", out var p) && p.TryGetDouble(out var value) ? value : 0;
            tasks.Add(new SoulSyncDownload(
                SoulSyncClient.ReadString(task, "status"),
                SoulSyncClient.ReadString(task, "track_name"),
                SoulSyncClient.ReadString(task, "artist_name"),
                SoulSyncClient.ReadString(task, "album_name"),
                System.Math.Clamp(progress / 100.0, 0, 1),
                SoulSyncClient.ReadString(task, "error")));
        }

        return tasks;
    }
}
