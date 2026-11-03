using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.NoriSync.Api;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Guards the total order the album sync feed pages over.
/// </summary>
/// <remarks>
/// A user who changed their music library's root folder saw every album twice. One of the
/// contributing causes was that <c>/albums</c> ordered by <c>DateCreated</c> alone: a library
/// rescan stamps nearly every album with the same creation date, and paging by offset over a
/// non-unique sort key can hand back one album on two pages while skipping another. These tests
/// pin the tiebreaker that makes the order total.
/// </remarks>
public class AlbumOrderingTests
{
    [Fact]
    public void CompareAlbumOrder_SortsNewestFirst()
    {
        var older = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid();

        Assert.True(ChangesController.CompareAlbumOrder(newer, id, older, id) < 0);
        Assert.True(ChangesController.CompareAlbumOrder(older, id, newer, id) > 0);
    }

    [Fact]
    public void CompareAlbumOrder_BreaksTiesOnId()
    {
        var sameDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = new Guid("00000000-0000-0000-0000-00000000000a");
        var b = new Guid("00000000-0000-0000-0000-00000000000b");

        Assert.True(ChangesController.CompareAlbumOrder(sameDate, a, sameDate, b) < 0);
        Assert.True(ChangesController.CompareAlbumOrder(sameDate, b, sameDate, a) > 0);
        Assert.Equal(0, ChangesController.CompareAlbumOrder(sameDate, a, sameDate, a));
    }

    [Fact]
    public void CompareAlbumOrder_IsATotalOrderWhenEveryAlbumSharesADateCreated()
    {
        // The exact shape of a library that was just bulk-rescanned after a root-folder change.
        var scanDate = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var albums = Enumerable.Range(0, 200)
            .Select(_ => (DateCreated: scanDate, Id: Guid.NewGuid()))
            .ToList();

        var sorted = albums.ToList();
        sorted.Sort((x, y) => ChangesController.CompareAlbumOrder(x.DateCreated, x.Id, y.DateCreated, y.Id));

        // No two distinct albums may compare equal, or their relative order is undefined and
        // the page window stops being reproducible between requests.
        for (var i = 0; i < sorted.Count - 1; i++)
        {
            Assert.True(
                ChangesController.CompareAlbumOrder(sorted[i].DateCreated, sorted[i].Id, sorted[i + 1].DateCreated, sorted[i + 1].Id) < 0,
                $"albums at {i} and {i + 1} compare equal, so paging over them is not reproducible");
        }

        // Re-sorting a differently-shuffled copy must land on the identical sequence.
        var reshuffled = albums.OrderBy(a => a.Id.GetHashCode()).ToList();
        reshuffled.Sort((x, y) => ChangesController.CompareAlbumOrder(x.DateCreated, x.Id, y.DateCreated, y.Id));
        Assert.Equal(sorted.Select(a => a.Id), reshuffled.Select(a => a.Id));
    }

    [Fact]
    public void OffsetPaging_OverTheTotalOrder_VisitsEveryAlbumExactlyOnce()
    {
        var scanDate = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var albums = Enumerable.Range(0, 137)
            .Select(_ => (DateCreated: scanDate, Id: Guid.NewGuid()))
            .ToList();

        const int PageSize = 10;
        var seen = new List<Guid>();

        // Each page is an independent sort + window, exactly as a separate HTTP request would be.
        for (var startIndex = 0; startIndex < albums.Count; startIndex += PageSize)
        {
            var page = albums.OrderBy(a => a, Comparer<(DateTime DateCreated, Guid Id)>.Create(
                    (x, y) => ChangesController.CompareAlbumOrder(x.DateCreated, x.Id, y.DateCreated, y.Id)))
                .Skip(startIndex)
                .Take(PageSize)
                .Select(a => a.Id);

            seen.AddRange(page);
        }

        Assert.Equal(albums.Count, seen.Count);
        Assert.Equal(albums.Count, seen.Distinct().Count());
        Assert.Equal(albums.Select(a => a.Id).OrderBy(id => id), seen.OrderBy(id => id));
    }

    [Fact]
    public void AlbumScopeCacheKey_IsOrderIndependent_ForTheSameLibrarySet()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var user = Guid.NewGuid();

        // Clients may send libraryIds in any order; a key that varied with it would recompute the
        // whole scope on alternating requests and quietly undo the memo.
        var first = ChangesController.BuildAlbumScopeCacheKey(user, new HashSet<Guid> { a, b });
        var second = ChangesController.BuildAlbumScopeCacheKey(user, new HashSet<Guid> { b, a });

        Assert.Equal(first, second);
    }

    [Fact]
    public void AlbumScopeCacheKey_DiffersByUserAndByLibrarySet()
    {
        var library = Guid.NewGuid();
        var otherLibrary = Guid.NewGuid();
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();

        var baseline = ChangesController.BuildAlbumScopeCacheKey(user, new HashSet<Guid> { library });

        // Library visibility is per user, so two users must never share a scope entry.
        Assert.NotEqual(baseline, ChangesController.BuildAlbumScopeCacheKey(otherUser, new HashSet<Guid> { library }));
        Assert.NotEqual(baseline, ChangesController.BuildAlbumScopeCacheKey(user, new HashSet<Guid> { otherLibrary }));
        Assert.NotEqual(baseline, ChangesController.BuildAlbumScopeCacheKey(user, new HashSet<Guid> { library, otherLibrary }));
    }
}
