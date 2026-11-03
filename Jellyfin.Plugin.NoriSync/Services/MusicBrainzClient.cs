using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Thin client for the public MusicBrainz JSON API at <c>musicbrainz.org/ws/2/</c>. Used
/// to back endpoints where Lidarr's own API can't help (e.g. listing a not-yet-imported
/// artist's discography). Serializes outbound calls through a semaphore to stay within
/// MusicBrainz's 1-request-per-second-per-User-Agent rate limit.
/// </summary>
public sealed class MusicBrainzClient : IDisposable
{
    private static readonly TimeSpan _minimumGap = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicBrainzClient"/> class.
    /// </summary>
    public MusicBrainzClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://musicbrainz.org/ws/2/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        // MusicBrainz requires a meaningful User-Agent that identifies the application.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-NoriSync/1.0 ( jellyfin-plugin-norisync )");
    }

    /// <summary>
    /// Issues a GET against a path relative to <c>/ws/2/</c>, waiting if needed to honour
    /// MusicBrainz's 1 req/s rate limit.
    /// </summary>
    /// <param name="relativePath">Path under <c>/ws/2/</c>, e.g. <c>release-group?artist=...&amp;fmt=json</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upstream response. Caller must dispose.</returns>
    public async Task<HttpResponseMessage> GetAsync(string relativePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastCall;
            if (elapsed < _minimumGap)
            {
                await Task.Delay(_minimumGap - elapsed, cancellationToken).ConfigureAwait(false);
            }

            var response = await _http.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
            _lastCall = DateTimeOffset.UtcNow;
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _http.Dispose();
        _gate.Dispose();
        _disposed = true;
    }
}
