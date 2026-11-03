using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A stored playlist cover design and the identity of the image it was baked into.
/// </summary>
/// <param name="PlaylistId">The playlist id.</param>
/// <param name="Design">The design document (JSON) as the client sent it.</param>
/// <param name="ImagePath">The primary image path at upload time.</param>
/// <param name="ImageDateModifiedTicks">That image's modification time, in ticks.</param>
public sealed record PlaylistCoverDesignRecord(Guid PlaylistId, string Design, string ImagePath, long ImageDateModifiedTicks);
