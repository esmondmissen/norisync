using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// A stand-in SoulSync: its own <c>/api/v1</c> (answers in its envelope) and, under
/// <c>/lidarr/api/v1</c>, its Lidarr-compatible API. Each route answers what a test sets, and
/// every request is recorded.
/// </summary>
internal sealed class FakeSoulSync : HttpMessageHandler
{
    public static readonly Uri BaseUri = new("http://soulsync:8008/");

    private readonly List<(string Method, string Path, string Query, string Body)> _requests = new();

    /// <summary>
    /// Gets the answers by "METHOD path" (path under /api/v1/, or "lidarr/…" for the compat API).
    /// </summary>
    public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = new();

    /// <summary>
    /// Gets the last <c>X-API-Key</c> sent.
    /// </summary>
    public string? LastApiKey { get; private set; }

    /// <summary>
    /// Gets the last <c>X-Profile-Id</c> sent to SoulSync's own API.
    /// </summary>
    public string? LastProfileId { get; private set; }

    /// <summary>
    /// Gets or sets an exception every request throws (an unreachable host).
    /// </summary>
    public Exception? Throw { get; set; }

    public static string Ok(string data) => "{ \"success\": true, \"data\": " + data + ", \"error\": null }";

    public static string Error(string code, string message)
        => "{ \"success\": false, \"data\": null, \"error\": { \"code\": \"" + code + "\", \"message\": \"" + message + "\" } }";

    public static SoulSyncClient Client(FakeSoulSync fake, int profileId = 1) => new(NullLogger<SoulSyncClient>.Instance, fake, BaseUri, "sk_test", profileId);

    public static LidarrClient CompatClient(FakeSoulSync fake)
        => new(NullLogger<LidarrClient>.Instance, fake, LidarrClient.SoulSyncCompatBaseUri(BaseUri.ToString())!, "sk_test", Models.RequestBackend.SoulSync);

    public void On(string methodAndPath, HttpStatusCode status, string body) => Routes[methodAndPath] = (status, body);

    public List<(string Method, string Path, string Query, string Body)> Requests(string method, string path)
    {
        lock (_requests)
        {
            return _requests.Where(r => r.Method == method && r.Path == path).ToList();
        }
    }

    public List<(string Method, string Path, string Query, string Body)> All()
    {
        lock (_requests)
        {
            return _requests.ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Throw != null)
        {
            throw Throw;
        }

        var uri = request.RequestUri!;
        var path = uri.AbsolutePath.StartsWith("/lidarr/api/v1/", StringComparison.Ordinal)
            ? "lidarr/" + uri.AbsolutePath["/lidarr/api/v1/".Length..]
            : uri.AbsolutePath["/api/v1/".Length..];
        var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        lock (_requests)
        {
            _requests.Add((request.Method.Method, path, uri.Query, body));
            LastApiKey = request.Headers.TryGetValues("X-API-Key", out var keys) ? keys.First()
                : request.Headers.TryGetValues("X-Api-Key", out var arrKeys) ? arrKeys.First() : null;
            if (request.Headers.TryGetValues("X-Profile-Id", out var profiles))
            {
                LastProfileId = profiles.First();
            }
        }

        return Routes.TryGetValue(request.Method.Method + " " + path, out var answer)
            ? new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(Error("NOT_FOUND", "no route"), Encoding.UTF8, "application/json") };
    }
}
