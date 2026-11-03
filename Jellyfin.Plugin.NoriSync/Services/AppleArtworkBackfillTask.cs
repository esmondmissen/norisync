using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Scheduled task that backfills animated album artwork from the Apple Music animated-artwork
/// service. When enabled, it walks every music album, resolves the configured motion-cover
/// variant(s) by artist/album name, and stores them through the same probe/commit pipeline as manual
/// uploads. Albums that already have the slot filled, and slots attempted recently (a negative cache
/// for the many albums Apple has no motion art for), are skipped.
/// </summary>
public class AppleArtworkBackfillTask : IScheduledTask
{
    // How long a recorded attempt suppresses re-tries. A miss stays a miss until Apple publishes
    // motion art, so re-querying every daily run would waste calls against the (scraping) service.
    // Shortest fetched clip treated as a real motion cover. Apple's run ~15-30s; a result at or
    // below this is a truncated download, not a short loop.
    private const double MinimumPlausibleSeconds = 2.0;

    private static readonly TimeSpan RetryWindow = TimeSpan.FromDays(30);

    private readonly ILibraryManager _libraryManager;
    private readonly AppleMusicArtworkClient _client;
    private readonly HlsVariantResolver _variants;
    private readonly ArtworkProbe _probe;
    private readonly ArtworkTranscoder _transcoder;
    private readonly ILogger<AppleArtworkBackfillTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppleArtworkBackfillTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="client">Client for the Apple animated-artwork service.</param>
    /// <param name="variants">Narrows an HLS master playlist to one rendition before the fetch.</param>
    /// <param name="probe">ffprobe wrapper used to validate fetched files.</param>
    /// <param name="transcoder">ffmpeg wrapper used to fetch and normalize motion covers.</param>
    /// <param name="logger">Logger for this task.</param>
    public AppleArtworkBackfillTask(
        ILibraryManager libraryManager,
        AppleMusicArtworkClient client,
        HlsVariantResolver variants,
        ArtworkProbe probe,
        ArtworkTranscoder transcoder,
        ILogger<AppleArtworkBackfillTask> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _variants = variants;
        _probe = probe;
        _transcoder = transcoder;
        _logger = logger;
    }

    private enum SlotKind
    {
        Square,
        Tall
    }

    private enum FetchOutcome
    {
        /// <summary>The asset was fetched, probed, and committed.</summary>
        Stored,

        /// <summary>The download, probe, or commit failed outright.</summary>
        Failed,

        /// <summary>The asset arrived far shorter than a real motion cover, so it was discarded.</summary>
        Truncated
    }

    /// <inheritdoc />
    public string Name => "NoriSync: Backfill Apple Animated Artwork";

    /// <inheritdoc />
    public string Key => "NoriSyncAppleArtworkBackfill";

    /// <inheritdoc />
    public string Description => "Fetches animated album covers from the Apple Music animated-artwork service for albums that don't have them.";

    /// <inheritdoc />
    public string Category => "NoriSync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var store = Plugin.Instance?.AnimatedArtworkStore;
        if (config == null || store == null)
        {
            _logger.LogWarning("Plugin not initialized; skipping Apple artwork backfill");
            return;
        }

        if (!config.AnimatedArtworkEnabled || !config.AppleArtworkBackfillEnabled)
        {
            _logger.LogInformation("Apple artwork backfill is disabled; nothing to do");
            progress.Report(100);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.AppleArtworkServiceUrl))
        {
            _logger.LogWarning("Apple artwork backfill is enabled but no service URL is configured");
            progress.Report(100);
            return;
        }

        var slots = ResolveSlots(config.AppleArtworkVariant);

        var albums = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            Recursive = true
        }).OfType<MusicAlbum>().ToList();

        var total = albums.Count;
        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        // Short-edge target for the fetch — the "1080p" number; zero or negative means take the
        // largest rendition offered.
        var targetShortEdge = config.AnimatedArtworkTargetResolution;
        var cap = config.AppleArtworkMaxPerRun > 0 ? config.AppleArtworkMaxPerRun : int.MaxValue;

        var queried = 0;
        var stored = 0;
        var misses = 0;
        var failures = 0;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report((double)i / total * 100);

            if (queried >= cap)
            {
                _logger.LogInformation("Apple artwork backfill hit the per-run cap of {Cap} albums; stopping early", cap);
                break;
            }

            var album = albums[i];

            // Which slots does this album still need? (missing AND not attempted recently)
            var needed = slots.Where(s =>
                store.Get(album.Id, s.ImageType) == null
                && !store.HasRecentAppleAttempt(album.Id, s.ImageType, RetryWindow)).ToList();
            if (needed.Count == 0)
            {
                continue;
            }

            var artist = FirstName(album.AlbumArtists) ?? FirstName(album.Artists);
            var title = album.Name;
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            queried++;

            AppleArtworkMatch? match;
            try
            {
                match = await _client.ResolveAsync(config.AppleArtworkServiceUrl, artist, title, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Apple artwork lookup failed for {Artist} - {Title}", artist, title);
                foreach (var slot in needed)
                {
                    store.RecordAppleAttempt(album.Id, slot.ImageType, "error");
                }

                failures++;
                continue;
            }

            foreach (var slot in needed)
            {
                var url = slot.Kind == SlotKind.Tall ? match?.TallUrl : match?.SquareUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    store.RecordAppleAttempt(album.Id, slot.ImageType, "nomatch");
                    misses++;
                    continue;
                }

                FetchOutcome outcome;
                try
                {
                    outcome = await FetchAndStoreAsync(store, album, slot.ImageType, url, targetShortEdge, ArtworkSizeLadder.BitrateKbpsFor(targetShortEdge), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to store Apple artwork for {Artist} - {Title} [{Slot}]", artist, title, slot.ImageType);
                    outcome = FetchOutcome.Failed;
                }

                if (outcome == FetchOutcome.Truncated)
                {
                    // No attempt recorded on purpose: the source is fine, the fetch was not. Marking
                    // it would suppress the retry for the whole window even once the cause is fixed.
                    failures++;
                    continue;
                }

                store.RecordAppleAttempt(album.Id, slot.ImageType, outcome == FetchOutcome.Stored ? "found" : "error");
                if (outcome == FetchOutcome.Stored)
                {
                    stored++;
                }
                else
                {
                    failures++;
                }
            }
        }

        progress.Report(100);
        _logger.LogInformation(
            "Apple artwork backfill complete: albumsQueried={Queried} stored={Stored} misses={Misses} failures={Failures} totalAlbums={Total}",
            queried,
            stored,
            misses,
            failures,
            total);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks + TimeSpan.FromMinutes(30).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    // Fetches one motion-cover URL to a faststart MP4, probes it, and commits it to the store.
    // Leaves no partial state on failure.
    private async Task<FetchOutcome> FetchAndStoreAsync(
        AnimatedArtworkStore store,
        MusicAlbum album,
        string imageType,
        string url,
        int targetShortEdge,
        int maxBitrateKbps,
        CancellationToken cancellationToken)
    {
        var tempPath = store.NewTempPath();
        var committed = false;
        try
        {
            // Hand ffmpeg one rendition, not the master listing all of them — see HlsVariantResolver.
            var rendition = await _variants.ResolveBestAsync(url, targetShortEdge, cancellationToken).ConfigureAwait(false);
            var fetchUrl = rendition?.Url ?? url;

            // When the resolver has already landed on the rendition nearest the target, re-encoding
            // it would burn CPU to produce a slightly worse picture at nearly the same size. Copy the
            // stream instead and let the master be Apple's own encode; the per-device sizes clients
            // ask for are cut from it later, on demand.
            var copied = CanCopy(rendition, targetShortEdge)
                && await _transcoder
                    .FetchCopyToMp4Async(fetchUrl, tempPath, rendition!.IsHevc, cancellationToken)
                    .ConfigureAwait(false);

            // No usable rendition, or the copy failed (an exotic codec the MP4 container will not
            // take, say): fall back to the re-encode, which normalizes anything.
            if (!copied
                && !await _transcoder.FetchToMp4Async(fetchUrl, tempPath, targetShortEdge, maxBitrateKbps, cancellationToken).ConfigureAwait(false))
            {
                return FetchOutcome.Failed;
            }

            var probed = await _probe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (probed == null)
            {
                return FetchOutcome.Failed;
            }

            // A motion cover runs for seconds, not frames. Anything this short means the fetch read
            // part of the source — a stray first segment, say — and storing it would both look
            // broken and, because the slot then counts as filled, stop the album being retried.
            if (probed.DurationSeconds is double duration && duration < MinimumPlausibleSeconds)
            {
                _logger.LogWarning(
                    "Discarding Apple {Slot} artwork for {Album}: fetched only {Duration:F2}s ({Width}x{Height}, {Bytes} bytes) from {Url}",
                    imageType,
                    album.Name,
                    duration,
                    probed.Width,
                    probed.Height,
                    new FileInfo(tempPath).Length,
                    url);
                return FetchOutcome.Truncated;
            }

            var sha = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
            var record = new AnimatedArtworkRecord
            {
                ItemId = album.Id,
                ItemType = "Album",
                ImageType = imageType,
                ContentType = "video/mp4",
                ByteLength = new FileInfo(tempPath).Length,
                DurationSeconds = probed.DurationSeconds,
                Width = probed.Width,
                Height = probed.Height,
                MoovAtFront = probed.MoovAtFront,
                Sha256 = sha,
                UploadedAt = DateTimeOffset.UtcNow,
                UploadedBy = null
            };

            store.Commit(record, tempPath);
            committed = true;

            _logger.LogInformation(
                "Stored Apple {Slot} artwork for {Album}: {Duration:F1}s {Width}x{Height} {Codec}, {Bytes} bytes ({Method})",
                imageType,
                album.Name,
                probed.DurationSeconds ?? 0,
                probed.Width,
                probed.Height,
                probed.VideoCodec,
                record.ByteLength,
                copied ? "copied" : "re-encoded");

            return FetchOutcome.Stored;
        }
        finally
        {
            if (!committed)
            {
                TryDeleteTemp(tempPath);
            }
        }
    }

    // Both variants is the default reading: an unset value means the setting predates this option
    // (an install upgraded from before the backfill existed), and an unrecognized one is a typo in
    // a hand-edited config. Neither should silently turn the whole task into a no-op.
    private SlotDescriptor[] ResolveSlots(string? variant)
    {
        var square = new SlotDescriptor(SlotKind.Square, ArtworkShape.SquareImageType);
        var tall = new SlotDescriptor(SlotKind.Tall, ArtworkShape.TallImageType);
        var normalized = (variant ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "square":
                return new[] { square };
            case "tall":
                return new[] { tall };
            case "both":
            case "":
                return new[] { square, tall };
            default:
                _logger.LogWarning(
                    "Apple artwork variant '{Variant}' is not recognized (expected Square, Tall, or Both); fetching both",
                    variant);
                return new[] { square, tall };
        }
    }

    // Whether the chosen rendition can be stored as-is rather than re-encoded to the target. No
    // rendition at all means the source was not a master playlist, so only the re-encode path can
    // bound what comes back; the size rule itself lives with the rest of the sizing rules.
    private static bool CanCopy(HlsRendition? rendition, int targetShortEdge)
        => rendition != null && ArtworkSizeLadder.CanCopyAtSize(rendition.ShortEdge, targetShortEdge);

    private static string? FirstName(IReadOnlyList<string>? names)
        => names?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp file {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp file {Path}", path);
        }
    }

    private readonly record struct SlotDescriptor(SlotKind Kind, string ImageType);
}
