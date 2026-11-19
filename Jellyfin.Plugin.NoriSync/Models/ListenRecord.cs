using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// One listen: a track a user played past the client's play threshold, on one device.
/// </summary>
/// <param name="ListenId">The client's id for the listen, unique per user. The same id on every device.</param>
/// <param name="ItemId">The audio item played.</param>
/// <param name="PlayedAt">When playback started, in Unix milliseconds on the listening device's clock.</param>
/// <param name="DeviceId">The device that played it.</param>
/// <param name="ContextType">What it was played from (<c>album</c>, <c>playlist</c>, …).</param>
/// <param name="ContextId">The id of that album / playlist / artist, when it is a server item.</param>
public sealed record ListenRecord(string ListenId, Guid ItemId, long PlayedAt, string DeviceId, string ContextType, string? ContextId);
