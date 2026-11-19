using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Thin forwarder to a Lidarr instance. Auth is Lidarr's API key, attached as
/// <c>X-Api-Key</c> on every upstream call. Base URL and key are read fresh from plugin
/// configuration on each request so admin edits take effect without a restart.
/// </summary>
public sealed class LidarrClient : IDisposable
{
    /// <summary>
    /// How long a probe waits for Lidarr. Much shorter than the general timeout: a probe runs
    /// while the admin types, and a wrong host should fail fast rather than hang the form.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly ILogger<LidarrClient> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LidarrClient"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public LidarrClient(ILogger<LidarrClient> logger)
        : this(logger, new HttpClientHandler())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LidarrClient"/> class with a custom
    /// message handler, so tests can stand in for Lidarr.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="handler">Handler that sends the HTTP requests. Disposed with the client.</param>
    internal LidarrClient(ILogger<LidarrClient> logger, HttpMessageHandler handler)
    {
        _logger = logger;
        _http = new HttpClient(handler, disposeHandler: true)
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

        var uri = TryParseBaseUri(config.LidarrBaseUrl);
        if (uri == null)
        {
            _logger.LogWarning("Lidarr base URL '{Url}' is not a valid http(s) URL", config.LidarrBaseUrl);
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
    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        string? queryString,
        HttpContent? body,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin configuration is not available");

        var baseUri = TryGetBaseUri()
            ?? throw new InvalidOperationException("Lidarr is not configured");

        return SendAsync(baseUri, config.LidarrApiKey, method, relativePath, queryString, body, completionOption, cancellationToken);
    }

    /// <summary>
    /// Tries a Lidarr URL and API key without saving them: checks that the URL is Lidarr's
    /// v1 API and that the key is accepted, then reads the root-folder and profile lists the
    /// configuration form offers as dropdowns. Upstream error bodies are never passed back.
    /// </summary>
    /// <param name="baseUrl">The Lidarr base URL to try.</param>
    /// <param name="apiKey">The API key to try.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The probe outcome; failures are reported in the result, not thrown.</returns>
    public async Task<LidarrProbeResponse> ProbeAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken)
    {
        var baseUri = TryParseBaseUri(baseUrl);
        if (baseUri == null)
        {
            return Failure(LidarrProbeStatus.InvalidUrl, "Enter a full http:// or https:// URL, such as http://lidarr:8686.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Failure(LidarrProbeStatus.MissingApiKey, "Enter the API key to connect.");
        }

        var key = apiKey.Trim();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var status = await GetJsonAsync(baseUri, key, "system/status", timeout.Token).ConfigureAwait(false);
            var statusFailure = CheckSystemStatus(status);
            if (statusFailure != null)
            {
                return statusFailure;
            }

            var version = ReadString(status.Body!.Value, "version");

            var rootTask = GetJsonAsync(baseUri, key, "rootfolder", timeout.Token);
            var qualityTask = GetJsonAsync(baseUri, key, "qualityprofile", timeout.Token);
            var metadataTask = GetJsonAsync(baseUri, key, "metadataprofile", timeout.Token);
            await Task.WhenAll(rootTask, qualityTask, metadataTask).ConfigureAwait(false);

            var rootFolders = ReadList(await rootTask.ConfigureAwait(false), "root folder", ReadRootFolder, out var failure);
            var qualityProfiles = ReadList(await qualityTask.ConfigureAwait(false), "quality profile", ReadProfile, out var qualityFailure);
            var metadataProfiles = ReadList(await metadataTask.ConfigureAwait(false), "metadata profile", ReadProfile, out var metadataFailure);
            failure ??= qualityFailure ?? metadataFailure;
            if (failure != null)
            {
                failure.Version = version;
                return failure;
            }

            return new LidarrProbeResponse
            {
                Status = LidarrProbeStatus.Connected,
                Version = version,
                RootFolders = rootFolders,
                QualityProfiles = qualityProfiles,
                MetadataProfiles = metadataProfiles,
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Lidarr probe of {Url} could not connect", baseUri);
            return Failure(LidarrProbeStatus.Unreachable, "The Jellyfin server couldn't reach that URL: " + ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                LidarrProbeStatus.TimedOut,
                string.Format(CultureInfo.InvariantCulture, "No answer from that URL within {0} seconds.", ProbeTimeout.TotalSeconds));
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
        _disposed = true;
    }

    /// <summary>
    /// Parses a Lidarr base URL into an absolute http(s) URI ending in a slash, so relative API
    /// paths resolve beneath any URL base (<c>http://host/lidarr/</c>).
    /// </summary>
    /// <param name="baseUrl">The URL as typed or stored.</param>
    /// <returns>The base URI, or null when the URL is empty or not http(s).</returns>
    internal static Uri? TryParseBaseUri(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri baseUri,
        string apiKey,
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

        var path = "api/v1/" + relativePath.TrimStart('/');
        var uri = new Uri(baseUri, path + (queryString ?? string.Empty));

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Api-Key", apiKey);
        if (body != null)
        {
            request.Content = body;
        }

        return await _http.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// GETs a Lidarr API path and reads the body as JSON. The body is only read on success and
    /// is null when it is not JSON, so a proxy's HTML login page reads as "not Lidarr".
    /// </summary>
    private async Task<ProbeReply> GetJsonAsync(Uri baseUri, string apiKey, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            baseUri,
            apiKey,
            HttpMethod.Get,
            path,
            queryString: null,
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new ProbeReply(response.StatusCode, null);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return new ProbeReply(response.StatusCode, doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return new ProbeReply(response.StatusCode, null);
        }
    }

    /// <summary>
    /// Turns the <c>system/status</c> reply into a failure, or null when it is Lidarr. Readarr
    /// and Prowlarr also serve <c>/api/v1</c>, so the app name is checked, not just the status.
    /// </summary>
    private static LidarrProbeResponse? CheckSystemStatus(ProbeReply reply)
    {
        if (reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Failure(LidarrProbeStatus.Unauthorized, "Lidarr rejected the API key.");
        }

        if (reply.Status == HttpStatusCode.NotFound)
        {
            return Failure(
                LidarrProbeStatus.NotLidarr,
                "No Lidarr API at that URL. Check the host and port, and include the URL base if Lidarr has one set.");
        }

        if ((int)reply.Status is < 200 or > 299)
        {
            return Failure(
                LidarrProbeStatus.UnexpectedResponse,
                string.Format(CultureInfo.InvariantCulture, "That URL answered with HTTP {0}.", (int)reply.Status));
        }

        if (reply.Body is not { ValueKind: JsonValueKind.Object } body)
        {
            return Failure(
                LidarrProbeStatus.NotLidarr,
                "Something answered at that URL, but not the Lidarr API. If Lidarr sits behind a proxy, check the URL base.");
        }

        var appName = ReadString(body, "appName");
        if (appName != null && !string.Equals(appName, "Lidarr", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(LidarrProbeStatus.NotLidarr, "That URL is a " + appName + " server, not Lidarr.");
        }

        if (appName == null && ReadString(body, "version") == null)
        {
            return Failure(LidarrProbeStatus.NotLidarr, "Something answered at that URL, but not the Lidarr API.");
        }

        return null;
    }

    /// <summary>
    /// Reads one list reply into dropdown choices. Entries missing their value are skipped
    /// rather than failing the whole probe.
    /// </summary>
    private static IReadOnlyList<LidarrChoice> ReadList(
        ProbeReply reply,
        string what,
        Func<JsonElement, LidarrChoice?> read,
        out LidarrProbeResponse? failure)
    {
        failure = null;
        if (reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            failure = Failure(LidarrProbeStatus.Unauthorized, "Lidarr rejected the API key when listing " + what + "s.");
            return Array.Empty<LidarrChoice>();
        }

        if ((int)reply.Status is < 200 or > 299 || reply.Body is not { ValueKind: JsonValueKind.Array } body)
        {
            failure = Failure(LidarrProbeStatus.UnexpectedResponse, "Connected, but Lidarr's " + what + " list couldn't be read.");
            return Array.Empty<LidarrChoice>();
        }

        var choices = new List<LidarrChoice>();
        foreach (var item in body.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && read(item) is { } choice)
            {
                choices.Add(choice);
            }
        }

        return choices;
    }

    private static LidarrChoice? ReadRootFolder(JsonElement item)
    {
        var path = ReadString(item, "path");
        return string.IsNullOrEmpty(path) ? null : new LidarrChoice { Value = path, Label = path };
    }

    private static LidarrChoice? ReadProfile(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var idValue))
        {
            return null;
        }

        var value = idValue.ToString(CultureInfo.InvariantCulture);
        return new LidarrChoice { Value = value, Label = ReadString(item, "name") ?? "Profile " + value };
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static LidarrProbeResponse Failure(LidarrProbeStatus status, string message)
        => new() { Status = status, Message = message };

    private readonly record struct ProbeReply(HttpStatusCode Status, JsonElement? Body);
}
