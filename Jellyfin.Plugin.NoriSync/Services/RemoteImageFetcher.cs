using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Fetches arbitrary external image URLs (Cover Art Archive, fanart.tv, etc.) for the
/// cover-art proxy. Separate from <see cref="LidarrClient"/> because its base URL is
/// dynamic per request and the requests don't carry a Lidarr API key.
/// </summary>
public sealed class RemoteImageFetcher : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteImageFetcher"/> class.
    /// </summary>
    public RemoteImageFetcher()
    {
        _http = new HttpClient
        {
            // CAA and fanart.tv can be slow on cache miss; 30s matches typical image-fetch
            // timeouts in the Jellyfin codebase.
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-NoriSync/1.0");
    }

    /// <summary>
    /// Issues a GET against an absolute URL with response headers read first so the body
    /// can be streamed back to the caller without buffering.
    /// </summary>
    /// <param name="url">The absolute image URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upstream response. Caller must dispose.</returns>
    public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _http.Dispose();
        _disposed = true;
    }
}
