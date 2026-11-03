using System;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Persists each Jellyfin user's DJ library id to a SQLite database. The id keys the user's
/// library on the external DJ service, so this store is its single source of truth: one id per
/// user on this server, shared by every device and surviving sign-out and reinstall.
/// </summary>
/// <remarks>
/// The library id is a bearer credential for the external service. It is minted from a random
/// GUID (never derived from anything guessable such as the server id), is never logged, and is
/// only ever returned to the user it belongs to.
/// </remarks>
public class DjLibraryStore : IDisposable
{
    private const string SourceAdopted = "adopted";
    private const string SourceMinted = "minted";

    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    /// <summary>
    /// Serialises all access to <see cref="_connection"/>. Reads take it too: a
    /// <see cref="SqliteConnection"/> is not thread-safe, and a read issued on the shared
    /// connection while <see cref="Claim"/> holds its transaction open would run inside it.
    /// </summary>
    private readonly object _connectionLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DjLibraryStore"/> class.
    /// </summary>
    /// <param name="dataPath">The directory path where the database file will be stored.</param>
    /// <param name="logger">Logger instance.</param>
    public DjLibraryStore(string dataPath, ILogger logger)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "djlibraries.db");
        _logger = logger;

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        _logger.LogInformation("DJ library database opened at {Path}", dbPath);
    }

    /// <summary>
    /// Returns the user's DJ library id, assigning one first if the user has none. An existing id
    /// always wins and <paramref name="proposedLibraryId"/> is ignored. Otherwise the proposal is
    /// adopted when it is a non-empty GUID that no other user already owns (so one user can never
    /// claim another's library), and a fresh random id is minted when it is not.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="proposedLibraryId">An id the client already holds and would like adopted, if any.</param>
    /// <param name="now">The current UTC time, recorded as the row's creation time.</param>
    /// <returns>The user's library id and how it was arrived at.</returns>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is <see cref="Guid.Empty"/>.</exception>
    public DjLibraryClaim Claim(Guid userId, Guid? proposedLibraryId, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A DJ library id can only be claimed for a real user.", nameof(userId));
        }

        DjLibraryClaim claim;
        lock (_connectionLock)
        {
            // BEGIN IMMEDIATE: take SQLite's write lock up front, so the existence checks below and
            // the insert see one consistent state even against another connection to this file.
            using var tx = _connection.BeginTransaction(deferred: false);

            var existing = SelectLibraryId(userId, tx);
            if (existing.HasValue)
            {
                claim = new DjLibraryClaim(existing.Value, DjLibraryClaimOutcome.Existing);
            }
            else
            {
                var adopt = proposedLibraryId.HasValue
                    && proposedLibraryId.Value != Guid.Empty
                    && !LibraryIdExists(proposedLibraryId.Value, tx);

                var candidate = adopt ? proposedLibraryId!.Value : Guid.NewGuid();

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        INSERT INTO DjLibraries (UserId, LibraryId, Source, CreatedAt)
                        VALUES ($userId, $libraryId, $source, $createdAt)
                        ON CONFLICT(UserId) DO NOTHING;";

                    cmd.Parameters.AddWithValue("$userId", FormatId(userId));
                    cmd.Parameters.AddWithValue("$libraryId", FormatId(candidate));
                    cmd.Parameters.AddWithValue("$source", adopt ? SourceAdopted : SourceMinted);
                    cmd.Parameters.AddWithValue("$createdAt", now.ToUnixTimeMilliseconds());
                    cmd.ExecuteNonQuery();
                }

                // Re-read rather than trust the insert: if a row for this user appeared anyway,
                // the stored id is the answer and this call didn't create anything.
                var stored = SelectLibraryId(userId, tx)
                    ?? throw new InvalidOperationException("DJ library row missing immediately after insert.");

                var outcome = stored != candidate
                    ? DjLibraryClaimOutcome.Existing
                    : adopt ? DjLibraryClaimOutcome.Adopted : DjLibraryClaimOutcome.Minted;
                claim = new DjLibraryClaim(stored, outcome);
            }

            tx.Commit();
        }

        // Log the outcome and the user, never the library id: it is a credential.
        if (claim.Outcome == DjLibraryClaimOutcome.Existing)
        {
            _logger.LogDebug("DJ library claim for user {UserId}: {Outcome}", userId, claim.Outcome);
        }
        else
        {
            _logger.LogInformation("DJ library claim for user {UserId}: {Outcome}", userId, claim.Outcome);
        }

        return claim;
    }

    /// <summary>
    /// Returns the user's DJ library id without assigning one.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The stored library id, or <see langword="null"/> if the user has none.</returns>
    public Guid? Get(Guid userId)
    {
        lock (_connectionLock)
        {
            return SelectLibraryId(userId, null);
        }
    }

    /// <summary>
    /// Forgets the user's DJ library id. The next <see cref="Claim"/> for the user assigns a new
    /// one (or adopts a proposal), as for a user who never had one.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns><see langword="true"/> if a row was removed.</returns>
    public bool Remove(Guid userId)
    {
        lock (_connectionLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM DjLibraries WHERE UserId = $userId";
            cmd.Parameters.AddWithValue("$userId", FormatId(userId));
            var removed = cmd.ExecuteNonQuery() > 0;

            if (removed)
            {
                _logger.LogInformation("Removed DJ library id for user {UserId}", userId);
            }

            return removed;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
        }
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS DjLibraries (
                UserId TEXT NOT NULL PRIMARY KEY,
                LibraryId TEXT NOT NULL UNIQUE,
                Source TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    private Guid? SelectLibraryId(Guid userId, SqliteTransaction? tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT LibraryId FROM DjLibraries WHERE UserId = $userId";
        cmd.Parameters.AddWithValue("$userId", FormatId(userId));

        return cmd.ExecuteScalar() is string value ? Guid.Parse(value, CultureInfo.InvariantCulture) : null;
    }

    private bool LibraryIdExists(Guid libraryId, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM DjLibraries WHERE LibraryId = $libraryId";
        cmd.Parameters.AddWithValue("$libraryId", FormatId(libraryId));
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// Canonical stored form for both id columns: lowercase hyphenated ("D"). Every write and every
    /// lookup goes through here, so the UNIQUE constraint on LibraryId compares like with like.
    /// </summary>
    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
