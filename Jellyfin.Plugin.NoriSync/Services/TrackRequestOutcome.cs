using Jellyfin.Plugin.NoriSync.Models;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// How a song request went.
/// </summary>
/// <param name="Kind">The outcome.</param>
/// <param name="Record">The caller's request, when it was made.</param>
/// <param name="Message">What to tell the user when it wasn't.</param>
public sealed record TrackRequestOutcome(TrackRequestOutcomeKind Kind, TrackRequestRecord? Record, string? Message)
{
    /// <summary>
    /// A request made.
    /// </summary>
    /// <param name="record">The caller's request.</param>
    /// <returns>The outcome.</returns>
    public static TrackRequestOutcome Requested(TrackRequestRecord record) => new(TrackRequestOutcomeKind.Requested, record, null);

    /// <summary>
    /// A request not made.
    /// </summary>
    /// <param name="kind">Why.</param>
    /// <param name="message">What to tell the user.</param>
    /// <returns>The outcome.</returns>
    public static TrackRequestOutcome Not(TrackRequestOutcomeKind kind, string? message) => new(kind, null, message);
}
