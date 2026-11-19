using System;
using System.Collections.Generic;
using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Which stored users the orphan prune removes (<see cref="PruneOrphansTask.DeletedUsers"/>).
/// </summary>
public sealed class PruneOrphansTaskTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carol = Guid.NewGuid();

    private static Func<Guid, object?> Existing(params Guid[] users)
    {
        var set = new HashSet<Guid>(users);
        return id => set.Contains(id) ? new object() : null;
    }

    [Fact]
    public void AUserJellyfinNoLongerHasIsPruned()
    {
        var deleted = PruneOrphansTask.DeletedUsers(new[] { Alice, Bob, Carol, Bob }, Existing(Alice, Carol));
        Assert.Equal(new[] { Bob }, deleted);
    }

    [Fact]
    public void NobodyIsPrunedWhenEveryUserExists()
    {
        Assert.Empty(PruneOrphansTask.DeletedUsers(new[] { Alice, Bob }, Existing(Alice, Bob)));
    }

    /// <summary>
    /// Every stored user missing reads as Jellyfin's user store being unreadable, not as every
    /// user deleted: nothing is wiped.
    /// </summary>
    [Fact]
    public void NobodyIsPrunedWhenNoStoredUserIsFound()
    {
        Assert.Empty(PruneOrphansTask.DeletedUsers(new[] { Alice, Bob }, Existing()));
    }

    [Fact]
    public void NothingStoredPrunesNothing()
    {
        Assert.Empty(PruneOrphansTask.DeletedUsers(Array.Empty<Guid>(), Existing(Alice)));
    }
}
