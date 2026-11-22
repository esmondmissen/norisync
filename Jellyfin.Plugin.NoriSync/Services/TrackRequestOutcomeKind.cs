namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// What happened to a song request.
/// </summary>
public enum TrackRequestOutcomeKind
{
    /// <summary>
    /// It is on SoulSync's wishlist.
    /// </summary>
    Requested,

    /// <summary>
    /// SoulSync refused it: the profile's request limit, a permission, a blocklist (422).
    /// </summary>
    Refused,

    /// <summary>
    /// SoulSync wrote nothing: it already has the song, or its ignore list holds it (409).
    /// </summary>
    NotAdded,

    /// <summary>
    /// MusicBrainz has no such recording (404).
    /// </summary>
    UnknownRecording,
}
