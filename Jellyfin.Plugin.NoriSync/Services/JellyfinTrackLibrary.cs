using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// <see cref="ITrackLibrary"/> over Jellyfin's own library.
/// </summary>
public sealed class JellyfinTrackLibrary : ITrackLibrary
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinTrackLibrary"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library.</param>
    public JellyfinTrackLibrary(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public bool Contains(string recordingMbid, string? title, string? artist, DateTimeOffset since)
    {
        // The recording id lives under MusicBrainzRecording (Picard's "MusicBrainz Track Id"),
        // not MusicBrainzTrack, which holds the release track id.
        var tagged = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.MusicBrainzRecording.ToString()] = recordingMbid },
            Recursive = true,
            Limit = 1,
        });
        if (tagged.Count > 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return false;
        }

        var added = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            MinDateCreated = since.UtcDateTime,
            Recursive = true,
        });
        return added.OfType<Audio>().Any(a => TrackMatch.SameSong(a.Name, a.Artists, title, artist));
    }
}
