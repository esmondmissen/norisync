using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Claim-semantics tests for <see cref="DjLibraryStore"/>. Lock in the invariants the DJ service
/// depends on: exactly one library id per user, an existing id always wins over a proposal, a
/// proposal is adopted only when it is a non-empty GUID no other user owns, concurrent first
/// claims converge on one id, and the id survives a restart.
/// </summary>
public sealed class DjLibraryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private DjLibraryStore _store;

    public DjLibraryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-djlibrary-tests-" + Guid.NewGuid().ToString("N"));
        _store = new DjLibraryStore(_tempDir, NullLogger.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Leave the temp dir on file-lock contention; nothing important leaks.
        }
    }

    [Fact]
    public void Claim_NoRow_WithProposal_AdoptsIt_AndStoresLowercase()
    {
        var user = Guid.NewGuid();
        var proposal = Guid.NewGuid();

        var claim = _store.Claim(user, proposal, Now);

        Assert.Equal(DjLibraryClaimOutcome.Adopted, claim.Outcome);
        Assert.Equal(proposal, claim.LibraryId);
        Assert.Equal(proposal, _store.Get(user));

        var row = ReadRawRow(user);
        Assert.NotNull(row);
        Assert.Equal(proposal.ToString("D").ToLowerInvariant(), row!.Value.LibraryId);
        Assert.Equal(user.ToString("D").ToLowerInvariant(), row.Value.UserId);
        Assert.Equal("adopted", row.Value.Source);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), row.Value.CreatedAt);
    }

    [Fact]
    public void Claim_NoRow_NoProposal_Mints()
    {
        var user = Guid.NewGuid();

        var claim = _store.Claim(user, null, Now);

        Assert.Equal(DjLibraryClaimOutcome.Minted, claim.Outcome);
        Assert.NotEqual(Guid.Empty, claim.LibraryId);
        Assert.Equal(claim.LibraryId, _store.Get(user));
        Assert.Equal("minted", ReadRawRow(user)!.Value.Source);
    }

    [Fact]
    public void Claim_NoRow_EmptyGuidProposal_Mints()
    {
        var user = Guid.NewGuid();

        var claim = _store.Claim(user, Guid.Empty, Now);

        Assert.Equal(DjLibraryClaimOutcome.Minted, claim.Outcome);
        Assert.NotEqual(Guid.Empty, claim.LibraryId);
    }

    [Fact]
    public void Claim_ExistingRow_IgnoresProposal()
    {
        var user = Guid.NewGuid();
        var first = _store.Claim(user, null, Now);

        var second = _store.Claim(user, Guid.NewGuid(), Now.AddMinutes(1));

        Assert.Equal(DjLibraryClaimOutcome.Existing, second.Outcome);
        Assert.Equal(first.LibraryId, second.LibraryId);
        Assert.Equal("minted", ReadRawRow(user)!.Value.Source);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), ReadRawRow(user)!.Value.CreatedAt);
    }

    [Fact]
    public void Claim_ProposalOwnedByAnotherUser_MintsADifferentId()
    {
        // A user must never be able to take over another user's library by proposing its id.
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var ownersId = _store.Claim(owner, null, Now).LibraryId;

        var claim = _store.Claim(other, ownersId, Now);

        Assert.Equal(DjLibraryClaimOutcome.Minted, claim.Outcome);
        Assert.NotEqual(ownersId, claim.LibraryId);
        Assert.Equal(ownersId, _store.Get(owner));
    }

    [Fact]
    public void Claim_IsIdempotent_AcrossCalls()
    {
        var user = Guid.NewGuid();
        var proposal = Guid.NewGuid();

        var first = _store.Claim(user, proposal, Now);
        var outcomes = Enumerable.Range(0, 5)
            .Select(i => _store.Claim(user, i % 2 == 0 ? null : Guid.NewGuid(), Now))
            .ToList();

        Assert.Equal(DjLibraryClaimOutcome.Adopted, first.Outcome);
        Assert.All(outcomes, c =>
        {
            Assert.Equal(proposal, c.LibraryId);
            Assert.Equal(DjLibraryClaimOutcome.Existing, c.Outcome);
        });
    }

    [Fact]
    public void Claim_ConcurrentFirstClaims_ConvergeOnOneId()
    {
        var user = Guid.NewGuid();
        var proposals = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        var results = new ConcurrentBag<DjLibraryClaim>();

        Parallel.For(0, proposals.Length, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i =>
            results.Add(_store.Claim(user, proposals[i], Now)));

        var ids = results.Select(r => r.LibraryId).Distinct().ToList();
        Assert.Single(ids);
        Assert.Contains(ids[0], proposals);
        Assert.Single(results, r => r.Outcome == DjLibraryClaimOutcome.Adopted);
        Assert.Equal(31, results.Count(r => r.Outcome == DjLibraryClaimOutcome.Existing));
        Assert.Equal(ids[0], _store.Get(user));
    }

    [Fact]
    public void Claim_ConcurrentFirstClaims_AcrossTwoConnections_ConvergeOnOneId()
    {
        // Two stores over one file have separate connections and separate in-process locks, so
        // only the BEGIN IMMEDIATE transaction keeps their check-then-insert sequences apart.
        using var second = new DjLibraryStore(_tempDir, NullLogger.Instance);
        var user = Guid.NewGuid();
        var results = new ConcurrentBag<DjLibraryClaim>();

        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i =>
            results.Add((i % 2 == 0 ? _store : second).Claim(user, Guid.NewGuid(), Now)));

        Assert.Single(results.Select(r => r.LibraryId).Distinct());
        Assert.Single(results, r => r.Outcome == DjLibraryClaimOutcome.Adopted);
    }

    [Fact]
    public void Claim_SurvivesDisposeAndReopen()
    {
        var user = Guid.NewGuid();
        var original = _store.Claim(user, null, Now).LibraryId;

        _store.Dispose();
        _store = new DjLibraryStore(_tempDir, NullLogger.Instance);

        Assert.Equal(original, _store.Get(user));
        var again = _store.Claim(user, Guid.NewGuid(), Now);
        Assert.Equal(DjLibraryClaimOutcome.Existing, again.Outcome);
        Assert.Equal(original, again.LibraryId);
    }

    [Fact]
    public void Claim_DifferentUsers_GetDifferentIds()
    {
        var a = _store.Claim(Guid.NewGuid(), null, Now);
        var b = _store.Claim(Guid.NewGuid(), null, Now);

        Assert.NotEqual(a.LibraryId, b.LibraryId);
        Assert.Equal(DjLibraryClaimOutcome.Minted, a.Outcome);
        Assert.Equal(DjLibraryClaimOutcome.Minted, b.Outcome);
    }

    [Fact]
    public void Claim_EmptyUserId_Throws()
    {
        Assert.Throws<ArgumentException>(() => _store.Claim(Guid.Empty, null, Now));
    }

    [Fact]
    public void Get_UnknownUser_ReturnsNull()
    {
        Assert.Null(_store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Remove_DeletesTheRow_AndNextClaimStartsFresh()
    {
        var user = Guid.NewGuid();
        var original = _store.Claim(user, null, Now).LibraryId;

        Assert.True(_store.Remove(user));
        Assert.Null(_store.Get(user));
        Assert.False(_store.Remove(user));

        var next = _store.Claim(user, null, Now);
        Assert.Equal(DjLibraryClaimOutcome.Minted, next.Outcome);
        Assert.NotEqual(original, next.LibraryId);
    }

    [Fact]
    public void Remove_FreesTheIdForReadoption()
    {
        // Once a user's row is gone its id is no longer "owned", so the same client re-proposing it
        // (e.g. after an admin reset) gets its existing upload back rather than a fresh library.
        var user = Guid.NewGuid();
        var original = _store.Claim(user, null, Now).LibraryId;
        _store.Remove(user);

        var claim = _store.Claim(user, original, Now);

        Assert.Equal(DjLibraryClaimOutcome.Adopted, claim.Outcome);
        Assert.Equal(original, claim.LibraryId);
    }

    private (string UserId, string LibraryId, string Source, long CreatedAt)? ReadRawRow(Guid userId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "djlibraries.db")}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT UserId, LibraryId, Source, CreatedAt FROM DjLibraries WHERE UserId = $userId";
        cmd.Parameters.AddWithValue("$userId", userId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
    }
}
