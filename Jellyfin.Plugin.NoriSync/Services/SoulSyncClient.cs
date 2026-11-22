using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Thin client for SoulSync's own API (<c>{base}/api/v1</c>), which song requests use. Its
/// album requests go through its Lidarr-compatible API instead
/// (<see cref="LidarrClient.ForSoulSyncCompat"/>). Base URL, key and profile are read fresh from
/// the configuration on each call. The key must be an admin key: a read key can only GET.
/// </summary>
public sealed class SoulSyncClient : IDisposable
{
    /// <summary>
    /// How long an ordinary call waits.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a probe waits: it runs while the admin types.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly ILogger<SoulSyncClient> _logger;
    private readonly Func<SoulSyncConnection?> _connection;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoulSyncClient"/> class for the configured
    /// SoulSync.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public SoulSyncClient(ILogger<SoulSyncClient> logger)
        : this(logger, new HttpClientHandler(), Configured)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SoulSyncClient"/> class bound to a fixed URL,
    /// key and profile, so tests can stand in for SoulSync with no plugin instance.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="handler">Handler that sends the HTTP requests. Disposed with the client.</param>
    /// <param name="baseUri">SoulSync's base URI, ending in a slash.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="profileId">The profile sent as <c>X-Profile-Id</c>.</param>
    internal SoulSyncClient(ILogger<SoulSyncClient> logger, HttpMessageHandler handler, Uri baseUri, string apiKey, int profileId = 1)
        : this(logger, handler, () => new SoulSyncConnection(baseUri, apiKey, profileId))
    {
    }

    private SoulSyncClient(ILogger<SoulSyncClient> logger, HttpMessageHandler handler, Func<SoulSyncConnection?> connection)
    {
        _logger = logger;
        _connection = connection;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>
    /// Gets a value indicating whether SoulSync is set up (URL and key).
    /// </summary>
    public bool IsConfigured => _connection() != null;

    /// <summary>
    /// Sends one call to <c>{base}/api/v1/{path}{query}</c> and unwraps SoulSync's envelope.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Path under <c>/api/v1/</c>, without a leading slash.</param>
    /// <param name="query">Query string including the leading <c>?</c>, or null.</param>
    /// <param name="body">JSON body, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="InvalidOperationException">SoulSync isn't set up.</exception>
    /// <exception cref="HttpRequestException">SoulSync couldn't be reached.</exception>
    public Task<SoulSyncReply> SendAsync(HttpMethod method, string path, string? query, JsonNode? body, CancellationToken cancellationToken)
    {
        var connection = _connection() ?? throw new InvalidOperationException("SoulSync is not configured");
        return SendAsync(connection, method, path, query, body, DefaultTimeout, cancellationToken);
    }

    /// <summary>
    /// Tries a SoulSync URL and key without saving them: its own API (and that the key is an
    /// admin key, by listing profiles, which a read key may not), then its Lidarr-compatible API
    /// for albums. Upstream error bodies are never passed back.
    /// </summary>
    /// <param name="baseUrl">The URL to try.</param>
    /// <param name="apiKey">The key to try.</param>
    /// <param name="compat">A client for the Lidarr-compatible API's probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome; failures are reported in it, not thrown.</returns>
    public async Task<SoulSyncProbeResponse> ProbeAsync(string? baseUrl, string? apiKey, LidarrClient compat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compat);

        var baseUri = LidarrClient.TryParseBaseUri(baseUrl);
        if (baseUri == null)
        {
            return Failure(SoulSyncProbeStatus.InvalidUrl, "Enter a full http:// or https:// URL, such as http://soulsync:8008.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Failure(SoulSyncProbeStatus.MissingApiKey, "Enter the API key to connect.");
        }

        var connection = new SoulSyncConnection(baseUri, apiKey.Trim(), 1);
        SoulSyncProbeResponse result;
        try
        {
            result = await ProbeOwnApiAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "SoulSync probe of {Url} could not connect", baseUri);
            return Failure(SoulSyncProbeStatus.Unreachable, "The Jellyfin server couldn't reach that URL: " + ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                SoulSyncProbeStatus.TimedOut,
                string.Format(CultureInfo.InvariantCulture, "No answer from that URL within {0} seconds.", ProbeTimeout.TotalSeconds));
        }

        if (result.Status != SoulSyncProbeStatus.Connected)
        {
            return result;
        }

        var compatUri = LidarrClient.SoulSyncCompatBaseUri(baseUrl)!;
        var albums = await compat.ProbeAsync(compatUri.ToString(), connection.ApiKey, cancellationToken).ConfigureAwait(false);
        result.AlbumsAvailable = albums.Status == LidarrProbeStatus.Connected;
        result.AlbumsMessage = albums.Status switch
        {
            LidarrProbeStatus.Connected => null,
            LidarrProbeStatus.NotLidarr => "Albums need SoulSync's Lidarr-compatible API. Turn it on in SoulSync's settings; songs work without it.",
            LidarrProbeStatus.Unauthorized => "SoulSync's Lidarr-compatible API refused the key. It takes an admin key.",
            _ => albums.Message,
        };
        return result;
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
    /// Unwraps a body as SoulSync's envelope.
    /// </summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="bytes">The body.</param>
    /// <returns>The reply.</returns>
    internal static SoulSyncReply Unwrap(HttpStatusCode status, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return new SoulSyncReply(status, null, null, null, false);
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success)
                || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return new SoulSyncReply(status, null, null, null, false);
            }

            JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
            string? code = null;
            string? message = null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = ReadString(error, "code");
                message = ReadString(error, "message");
            }

            return new SoulSyncReply(status, data, code, message, true);
        }
        catch (JsonException)
        {
            return new SoulSyncReply(status, null, null, null, false);
        }
    }

    /// <summary>
    /// Reads a string property, or null.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="property">The property.</param>
    /// <returns>The value, or null when absent or not a string.</returns>
    internal static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static SoulSyncConnection? Configured()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.SoulSyncApiKey))
        {
            return null;
        }

        return LidarrClient.TryParseBaseUri(config.SoulSyncBaseUrl) is { } uri
            ? new SoulSyncConnection(uri, config.SoulSyncApiKey.Trim(), Math.Max(1, config.SoulSyncProfileId))
            : null;
    }

    private async Task<SoulSyncProbeResponse> ProbeOwnApiAsync(SoulSyncConnection connection, CancellationToken cancellationToken)
    {
        var status = await SendAsync(connection, HttpMethod.Get, "system/status", null, null, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (status.Status is HttpStatusCode.Unauthorized || (status.Status == HttpStatusCode.Forbidden && status.ErrorCode == "INVALID_KEY"))
        {
            return Failure(SoulSyncProbeStatus.Unauthorized, "SoulSync rejected the API key.");
        }

        if (status.Status == HttpStatusCode.NotFound || !status.IsEnvelope)
        {
            return Failure(
                SoulSyncProbeStatus.NotSoulSync,
                "Something answered at that URL, but not SoulSync's API. Check the host and port (SoulSync's default is 8008).");
        }

        if (!status.IsSuccess)
        {
            return Failure(
                SoulSyncProbeStatus.UnexpectedResponse,
                string.Format(CultureInfo.InvariantCulture, "SoulSync answered with HTTP {0}.", (int)status.Status));
        }

        // A read key passes the status check; only an admin key may list profiles.
        var profiles = await SendAsync(connection, HttpMethod.Get, "profiles", null, null, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (profiles.Status == HttpStatusCode.Forbidden && profiles.ErrorCode == "INSUFFICIENT_SCOPE")
        {
            return Failure(SoulSyncProbeStatus.ReadOnlyKey, "That key is read-only. Generate an admin key in SoulSync's settings.");
        }

        if (!profiles.IsSuccess)
        {
            return Failure(
                SoulSyncProbeStatus.UnexpectedResponse,
                string.Format(CultureInfo.InvariantCulture, "Connected, but SoulSync's profile list couldn't be read (HTTP {0}).", (int)profiles.Status));
        }

        return new SoulSyncProbeResponse
        {
            Status = SoulSyncProbeStatus.Connected,
            Profiles = ReadProfiles(profiles.Data),
        };
    }

    private static List<LidarrChoice> ReadProfiles(JsonElement? data)
    {
        var choices = new List<LidarrChoice>();
        if (data is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("profiles", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return choices;
        }

        foreach (var profile in list.EnumerateArray())
        {
            if (profile.ValueKind == JsonValueKind.Object
                && profile.TryGetProperty("id", out var id)
                && id.TryGetInt32(out var value))
            {
                var text = value.ToString(CultureInfo.InvariantCulture);
                choices.Add(new LidarrChoice { Value = text, Label = ReadString(profile, "name") ?? "Profile " + text });
            }
        }

        return choices;
    }

    private async Task<SoulSyncReply> SendAsync(
        SoulSyncConnection connection,
        HttpMethod method,
        string path,
        string? query,
        JsonNode? body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var uri = new Uri(connection.BaseUri, "api/v1/" + path.TrimStart('/') + (query ?? string.Empty));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-API-Key", connection.ApiKey);
        request.Headers.Add("X-Profile-Id", connection.ProfileId.ToString(CultureInfo.InvariantCulture));
        if (body != null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
        return Unwrap(response.StatusCode, bytes);
    }

    private static SoulSyncProbeResponse Failure(SoulSyncProbeStatus status, string message)
        => new() { Status = status, Message = message };
}
