namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// The <c>TakeOver</c> of a report: the reporting device is taking a session another device
/// holds.
/// </summary>
public sealed class PlaybackTakeOverRequest
{
    /// <summary>
    /// Gets or sets the session taken.
    /// </summary>
    public string? SessionId { get; set; }
}
