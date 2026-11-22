using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// One interactive-search result (<c>GET /api/norisync/lidarr/albums/{mbid}/releases</c>), cut
/// down from Lidarr's release resource to what a picker shows and a grab needs. Lidarr's
/// <c>downloadUrl</c>, <c>magnetUrl</c>, <c>infoUrl</c> and <c>commentUrl</c> are never passed
/// on: indexers put their API key in them, and any user may run a search when
/// <c>LidarrRequireAdmin</c> is off.
/// </summary>
public sealed class ReleaseCandidate
{
    /// <summary>
    /// Gets or sets Lidarr's id for the result, sent back to grab it (with <see cref="IndexerId"/>).
    /// </summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the indexer's Lidarr id.
    /// </summary>
    public int IndexerId { get; set; }

    /// <summary>
    /// Gets or sets the indexer's name.
    /// </summary>
    public string? Indexer { get; set; }

    /// <summary>
    /// Gets or sets the release title as the indexer lists it.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size in bytes, or null when the indexer gave none.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Gets or sets Lidarr's quality name ("FLAC", "MP3-320").
    /// </summary>
    public string? Quality { get; set; }

    /// <summary>
    /// Gets or sets <c>torrent</c> or <c>usenet</c>.
    /// </summary>
    public string? Protocol { get; set; }

    /// <summary>
    /// Gets or sets the seeders (torrents only).
    /// </summary>
    public int? Seeders { get; set; }

    /// <summary>
    /// Gets or sets the leechers (torrents only).
    /// </summary>
    public int? Leechers { get; set; }

    /// <summary>
    /// Gets or sets the age in hours since the indexer published it.
    /// </summary>
    public double? AgeHours { get; set; }

    /// <summary>
    /// Gets or sets the release group.
    /// </summary>
    public string? ReleaseGroup { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Lidarr would take this one on its own.
    /// </summary>
    public bool Approved { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Lidarr skips it only for now (a delay profile).
    /// </summary>
    public bool TemporarilyRejected { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Lidarr would skip it.
    /// </summary>
    public bool Rejected { get; set; }

    /// <summary>
    /// Gets or sets why Lidarr would skip it.
    /// </summary>
    public IReadOnlyList<string> Rejections { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Reads Lidarr's <c>GET release</c> array, in Lidarr's order (its ranking).
    /// </summary>
    /// <param name="releases">The array.</param>
    /// <returns>The candidates; entries without a guid or indexer are skipped.</returns>
    public static IReadOnlyList<ReleaseCandidate> FromLidarr(JsonElement releases)
    {
        var candidates = new List<ReleaseCandidate>();
        if (releases.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || ReadString(release, "guid") is not { Length: > 0 } guid
                || ReadInt(release, "indexerId") is not int indexerId)
            {
                continue;
            }

            candidates.Add(new ReleaseCandidate
            {
                Guid = guid,
                IndexerId = indexerId,
                Indexer = ReadString(release, "indexer"),
                Title = ReadString(release, "title") ?? string.Empty,
                Size = release.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) && bytes > 0 ? bytes : null,
                Quality = ReadQualityName(release),
                Protocol = ReadString(release, "protocol"),
                Seeders = ReadInt(release, "seeders"),
                Leechers = ReadInt(release, "leechers"),
                AgeHours = release.TryGetProperty("ageHours", out var age) && age.TryGetDouble(out var hours) ? hours : null,
                ReleaseGroup = ReadString(release, "releaseGroup"),
                Approved = ReadBool(release, "approved"),
                TemporarilyRejected = ReadBool(release, "temporarilyRejected"),
                Rejected = ReadBool(release, "rejected"),
                Rejections = ReadStrings(release, "rejections"),
            });
        }

        return candidates;
    }

    // `quality` is `{ quality: { id, name }, revision }`.
    private static string? ReadQualityName(JsonElement release)
        => release.TryGetProperty("quality", out var outer)
            && outer.ValueKind == JsonValueKind.Object
            && outer.TryGetProperty("quality", out var inner)
            && inner.ValueKind == JsonValueKind.Object
                ? ReadString(inner, "name")
                : null;

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    private static bool ReadBool(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static List<string> ReadStrings(JsonElement element, string property)
    {
        var strings = new List<string>();
        if (element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                {
                    strings.Add(text);
                }
            }
        }

        return strings;
    }
}
