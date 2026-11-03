using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Thin forwarder to a Lidarr instance. Auth is Lidarr's API key, attached as
/// <c>X-Api-Key</c> on every upstream call. Base URL and key are read fresh from plugin
/// configuration on each request so admin edits take effect without a restart.
/// </summary>
public sealed class LidarrClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<LidarrClient> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LidarrClient"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public LidarrClient(ILogger<LidarrClient> logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            // Lidarr's MusicBrainz-backed lookups can be slow on cold cache.
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    /// <summary>
    /// Resolves the configured Lidarr base URI, or null when URL/key is missing or invalid.
    /// </summary>
    /// <returns>The base URI ending in a slash, or null when the proxy is disabled.</returns>
    public Uri? TryGetBaseUri()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.LidarrBaseUrl)
            || string.IsNullOrWhiteSpace(config.LidarrApiKey))
        {
            return null;
        }

        if (!Uri.TryCreate(config.LidarrBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Lidarr base URL '{Url}' is not a valid http(s) URL", config.LidarrBaseUrl);
            return null;
        }

        return uri;
    }

    /// <summary>
    /// Sends a request to <c>{base}/api/v1/{relativePath}{queryString}</c> with the API key
    /// attached, returning the raw response so callers can stream or buffer as appropriate.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="relativePath">Path under <c>/api/v1/</c>, without a leading slash.</param>
    /// <param name="queryString">Query string including the leading <c>?</c>, or null.</param>
    /// <param name="body">Optional request body. The caller retains ownership.</param>
    /// <param name="completionOption">When the returned task completes. Use <c>ResponseHeadersRead</c> for streamed payloads such as cover art.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upstream response. Caller must dispose.</returns>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        string? queryString,
        HttpContent? body,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin configuration is not available");

        var baseUri = TryGetBaseUri()
            ?? throw new InvalidOperationException("Lidarr is not configured");

        var path = "api/v1/" + relativePath.TrimStart('/');
        var uri = new Uri(baseUri, path + (queryString ?? string.Empty));

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Api-Key", config.LidarrApiKey);
        if (body != null)
        {
            request.Content = body;
        }

        return await _http.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
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
