using System;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Whether a requested song has reached the Jellyfin library.
/// </summary>
public interface ITrackLibrary
{
    /// <summary>
    /// Whether the library has the song: an audio item tagged with the recording, or (a file
    /// tagged with another release's recording id, or none) one added since the request with the
    /// same title and artist.
    /// </summary>
    /// <param name="recordingMbid">The MusicBrainz recording id.</param>
    /// <param name="title">The song's title.</param>
    /// <param name="artist">The song's artist.</param>
    /// <param name="since">When it was requested.</param>
    /// <returns>True when it is there.</returns>
    bool Contains(string recordingMbid, string? title, string? artist, DateTimeOffset since);
}
