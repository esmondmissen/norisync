using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Produces and caches downscaled renditions of an animated-artwork master, so a client can ask for
/// the size its screen needs without the server re-encoding on every request. The first request for
/// a size runs one ffmpeg pass and stores the result; every later request — including the range
/// requests a video player issues while playing — is served from that file, keeping HTTP range
/// support and a stable ETag. Renditions are only ever smaller than the master, so nothing is
/// upscaled.
/// </summary>
public sealed class ArtworkDerivativeCache : IDisposable
{
    // Derivations are serialized through a fixed set of gates rather than one per key: a gate per
    // (item, slot, size) would accumulate for the life of the process across a large library, while
    // striping bounds the count and costs only an occasional wait between unrelated sizes.
    private const int GateCount = 32;

    private readonly SemaphoreSlim[] _gates = new SemaphoreSlim[GateCount];
    private readonly ArtworkTranscoder _transcoder;
    private readonly ArtworkProbe _probe;
    private readonly ILogger<ArtworkDerivativeCache> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkDerivativeCache"/> class.
    /// </summary>
    /// <param name="transcoder">ffmpeg wrapper used to downscale the master.</param>
    /// <param name="probe">ffprobe wrapper used to read back the rendition.</param>
    /// <param name="logger">Logger instance.</param>
    public ArtworkDerivativeCache(ArtworkTranscoder transcoder, ArtworkProbe probe, ILogger<ArtworkDerivativeCache> logger)
    {
        _transcoder = transcoder;
        _probe = probe;
        _logger = logger;

        for (var i = 0; i < GateCount; i++)
        {
            _gates[i] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>
    /// Returns the cached rendition of <paramref name="master"/> at the given ladder step, producing
    /// it first if this is the first request for that size. Returns <see langword="null"/> when the
    /// rendition could not be produced, which the caller should treat as "serve the master" rather
    /// than as an error — a missing size is a quality regression, not a failure.
    /// </summary>
    /// <param name="store">The artwork store holding masters and renditions.</param>
    /// <param name="master">The master to derive from.</param>
    /// <param name="shortEdge">The ladder step to produce.</param>
    /// <param name="maxBitrateKbps">Bitrate ceiling passed to the encoder; zero or negative means CRF only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendition, or <see langword="null"/>.</returns>
    public async Task<AnimatedArtworkDerivative?> GetOrCreateAsync(
        AnimatedArtworkStore store,
        AnimatedArtworkRecord master,
        int shortEdge,
        int maxBitrateKbps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(master);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cached = store.GetDerivative(master.ItemId, master.ImageType, shortEdge);
        if (IsUsable(cached, master))
        {
            return cached;
        }

        var gate = _gates[GateIndex(master.ItemId, master.ImageType, shortEdge)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another request may have produced it while this one waited.
            cached = store.GetDerivative(master.ItemId, master.ImageType, shortEdge);
            if (IsUsable(cached, master))
            {
                return cached;
            }

            return await CreateAsync(store, master, shortEdge, maxBitrateKbps, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var gate in _gates)
        {
            gate.Dispose();
        }

        _disposed = true;
    }

    // A rendition is only good for the master it was cut from: an upload that replaced the master
    // normally drops its renditions, and this catches the case where that cleanup did not land.
    private static bool IsUsable(AnimatedArtworkDerivative? derivative, AnimatedArtworkRecord master)
        => derivative != null
            && string.Equals(derivative.SourceSha256, master.Sha256, StringComparison.Ordinal)
            && File.Exists(derivative.FilePath);

    private static int GateIndex(Guid itemId, string imageType, int shortEdge)
    {
        var hash = HashCode.Combine(itemId, imageType, shortEdge);
        return (hash & int.MaxValue) % GateCount;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<AnimatedArtworkDerivative?> CreateAsync(
        AnimatedArtworkStore store,
        AnimatedArtworkRecord master,
        int shortEdge,
        int maxBitrateKbps,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(master.FilePath))
        {
            return null;
        }

        var tempPath = store.NewDerivativeTempPath();
        var committed = false;
        try
        {
            var started = DateTimeOffset.UtcNow;
            if (!await _transcoder.TranscodeAsync(master.FilePath, tempPath, shortEdge, maxBitrateKbps, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Failed to derive {ShortEdge}px artwork for {ItemId} [{Slot}]; serving the master instead",
                    shortEdge,
                    master.ItemId,
                    master.ImageType);
                return null;
            }

            var probed = await _probe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (probed == null)
            {
                return null;
            }

            var derivative = new AnimatedArtworkDerivative
            {
                ItemId = master.ItemId,
                ImageType = master.ImageType,
                ShortEdge = shortEdge,
                ContentType = "video/mp4",
                ByteLength = new FileInfo(tempPath).Length,
                DurationSeconds = probed.DurationSeconds,
                Width = probed.Width,
                Height = probed.Height,
                Sha256 = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false),
                SourceSha256 = master.Sha256,
                CreatedAt = DateTimeOffset.UtcNow
            };

            store.CommitDerivative(derivative, tempPath);
            committed = true;

            _logger.LogInformation(
                "Derived {ShortEdge}px artwork for {ItemId} [{Slot}]: {Width}x{Height}, {Bytes} bytes in {Elapsed}ms",
                shortEdge,
                master.ItemId,
                master.ImageType,
                derivative.Width,
                derivative.Height,
                derivative.ByteLength,
                (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds);

            return derivative;
        }
        finally
        {
            if (!committed)
            {
                TryDeleteTemp(tempPath);
            }
        }
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
}
