using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="LidarrClient.ProbeAsync"/>, which the configuration page calls with an
/// unsaved URL and key to fill its dropdowns. A fake handler stands in for Lidarr, keyed by the
/// path under <c>/api/v1/</c>.
/// </summary>
public sealed class LidarrProbeTests
{
    private const string LidarrStatus = """{ "appName": "Lidarr", "version": "2.9.6.4552" }""";

    private const string RootFolders = """[ { "id": 1, "name": "Music", "path": "/music" }, { "id": 2, "path": "/archive" } ]""";

    private const string QualityProfiles = """[ { "id": 1, "name": "Any" }, { "id": 4, "name": "Lossless" } ]""";

    private const string MetadataProfiles = """[ { "id": 1, "name": "Standard" } ]""";

    /// <summary>
    /// A reachable Lidarr with a good key yields every list mapped to dropdown choices, with the
    /// key sent as <c>X-Api-Key</c> and a URL base kept in front of <c>/api/v1</c>.
    /// </summary>
    [Fact]
    public async Task Probe_Lidarr_ReturnsListsAndVersion()
    {
        var handler = FakeLidarr.Healthy();
        using var client = NewClient(handler);

        var result = await client.ProbeAsync(" http://lidarr:8686/base/ ", " secret ", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.Connected, result.Status);
        Assert.Null(result.Message);
        Assert.Equal("2.9.6.4552", result.Version);
        Assert.Equal(new[] { "/music", "/archive" }, result.RootFolders.Select(c => c.Value));
        Assert.Equal(new[] { "/music", "/archive" }, result.RootFolders.Select(c => c.Label));
        Assert.Equal(new[] { ("1", "Any"), ("4", "Lossless") }, result.QualityProfiles.Select(c => (c.Value, c.Label)));
        Assert.Equal(new[] { ("1", "Standard") }, result.MetadataProfiles.Select(c => (c.Value, c.Label)));

        Assert.All(handler.Requests, r => Assert.Equal("secret", r.ApiKey));
        Assert.Contains(handler.Requests, r => r.Uri == "http://lidarr:8686/base/api/v1/system/status");
        Assert.Contains(handler.Requests, r => r.Uri == "http://lidarr:8686/base/api/v1/metadataprofile");
    }

    /// <summary>
    /// URLs that are not absolute http(s) are refused before any request is sent. A bare
    /// <c>host:port</c> parses as a URI with the host as its scheme, so it must be caught too.
    /// </summary>
    /// <param name="url">The URL to try.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("lidarr:8686")]
    [InlineData("ftp://lidarr:8686")]
    [InlineData("/relative/path")]
    public async Task Probe_InvalidUrl_FailsWithoutCalling(string? url)
    {
        var handler = FakeLidarr.Healthy();
        using var client = NewClient(handler);

        var result = await client.ProbeAsync(url, "secret", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.InvalidUrl, result.Status);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// A URL with no key asks for the key and sends nothing.
    /// </summary>
    [Fact]
    public async Task Probe_NoApiKey_FailsWithoutCalling()
    {
        var handler = FakeLidarr.Healthy();
        using var client = NewClient(handler);

        var result = await client.ProbeAsync("http://lidarr:8686", " ", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.MissingApiKey, result.Status);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Lidarr's 401 for a wrong key is reported as a rejected key, and the lists are not read.
    /// </summary>
    [Fact]
    public async Task Probe_WrongKey_IsUnauthorized()
    {
        var handler = new FakeLidarr(_ => Respond(HttpStatusCode.Unauthorized, string.Empty));
        using var client = NewClient(handler);

        var result = await client.ProbeAsync("http://lidarr:8686", "wrong", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.Unauthorized, result.Status);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// Prowlarr and Readarr also serve <c>/api/v1/system/status</c>; the app name tells them apart.
    /// </summary>
    [Fact]
    public async Task Probe_OtherArrApp_IsNotLidarr()
    {
        var handler = new FakeLidarr(_ => Respond(HttpStatusCode.OK, """{ "appName": "Prowlarr", "version": "1.20.0" }"""));
        using var client = NewClient(handler);

        var result = await client.ProbeAsync("http://prowlarr:9696", "secret", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.NotLidarr, result.Status);
        Assert.Contains("Prowlarr", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 200 that isn't JSON (a reverse proxy's login page) and a 404 (wrong port or URL base)
    /// both read as "not Lidarr".
    /// </summary>
    /// <param name="status">The status code the URL answers with.</param>
    /// <param name="body">The body it answers with.</param>
    [Theory]
    [InlineData(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>")]
    [InlineData(HttpStatusCode.OK, """{ "something": "else" }""")]
    [InlineData(HttpStatusCode.NotFound, "Not Found")]
    public async Task Probe_NotTheLidarrApi_IsNotLidarr(HttpStatusCode status, string body)
    {
        var handler = new FakeLidarr(_ => Respond(status, body));
        using var client = NewClient(handler);

        var result = await client.ProbeAsync("http://lidarr:8686", "secret", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.NotLidarr, result.Status);
    }

    /// <summary>
    /// A connection failure is reported in the result rather than thrown.
    /// </summary>
    [Fact]
    public async Task Probe_ConnectionRefused_IsUnreachable()
    {
        var handler = new FakeLidarr(_ => throw new HttpRequestException("Connection refused (lidarr:8686)"));
        using var client = NewClient(handler);

        var result = await client.ProbeAsync("http://lidarr:8686", "secret", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.Unreachable, result.Status);
        Assert.Contains("Connection refused", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancellation the caller did not ask for is the probe's own timeout firing.
    /// </summary>
    [Fact]
    public async Task Probe_UpstreamTimeout_IsTimedOut()
    {
        var handler = new FakeLidarr(_ => throw new TaskCanceledException("timed out"));
        using var client = NewClient(handler);

        var result = await client.ProbeAsync("http://lidarr:8686", "secret", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.TimedOut, result.Status);
    }

    /// <summary>
    /// When the caller goes away (the admin kept typing and the page dropped the request), the
    /// cancellation propagates instead of being reported as a timeout.
    /// </summary>
    [Fact]
    public async Task Probe_CallerCancels_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var client = NewClient(FakeLidarr.Healthy());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ProbeAsync("http://lidarr:8686", "secret", cts.Token));
    }

    /// <summary>
    /// A list that fails after the status check succeeded reports which list, and keeps the
    /// version so the page can still say which Lidarr it reached.
    /// </summary>
    [Fact]
    public async Task Probe_ListFails_IsUnexpectedResponseWithVersion()
    {
        var handler = new FakeLidarr(path => path == "qualityprofile"
            ? Respond(HttpStatusCode.InternalServerError, "boom")
            : FakeLidarr.HealthyResponse(path));
        using var client = NewClient(handler);

        var result = await client.ProbeAsync("http://lidarr:8686", "secret", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.UnexpectedResponse, result.Status);
        Assert.Contains("quality profile", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", result.Message, StringComparison.Ordinal);
        Assert.Equal("2.9.6.4552", result.Version);
        Assert.Empty(result.RootFolders);
    }

    /// <summary>
    /// Entries without a usable value are skipped; a profile without a name gets a fallback label.
    /// </summary>
    [Fact]
    public async Task Probe_MalformedEntries_AreSkipped()
    {
        var handler = new FakeLidarr(path => path switch
        {
            "rootfolder" => Respond(HttpStatusCode.OK, """[ { "path": "" }, { "id": 3 }, { "path": "/music" }, 7 ]"""),
            "qualityprofile" => Respond(HttpStatusCode.OK, """[ { "name": "No id" }, { "id": "2", "name": "String id" }, { "id": 5 } ]"""),
            _ => FakeLidarr.HealthyResponse(path),
        });
        using var client = NewClient(handler);

        var result = await client.ProbeAsync("http://lidarr:8686", "secret", CancellationToken.None);

        Assert.Equal(LidarrProbeStatus.Connected, result.Status);
        Assert.Equal(new[] { "/music" }, result.RootFolders.Select(c => c.Value));
        Assert.Equal(new[] { ("5", "Profile 5") }, result.QualityProfiles.Select(c => (c.Value, c.Label)));
    }

    /// <summary>
    /// The URL parser shared with the saved-config path keeps a URL base and adds the trailing
    /// slash relative paths need.
    /// </summary>
    [Fact]
    public void TryParseBaseUri_KeepsUrlBase()
    {
        Assert.Equal("https://media.example/lidarr/", LidarrClient.TryParseBaseUri("https://media.example/lidarr")?.ToString());
        Assert.Equal("http://lidarr:8686/", LidarrClient.TryParseBaseUri(" http://lidarr:8686/// ")?.ToString());
    }

    private static LidarrClient NewClient(FakeLidarr handler)
        => new(NullLogger<LidarrClient>.Instance, handler);

    private static HttpResponseMessage Respond(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record SentRequest(string Uri, string? ApiKey);

    /// <summary>
    /// Answers each request from a function of the path under <c>/api/v1/</c>, recording what was sent.
    /// </summary>
    private sealed class FakeLidarr : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;
        private readonly List<SentRequest> _requests = new();

        public FakeLidarr(Func<string, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public IReadOnlyList<SentRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        public static FakeLidarr Healthy() => new(HealthyResponse);

        public static HttpResponseMessage HealthyResponse(string path) => path switch
        {
            "system/status" => Respond(HttpStatusCode.OK, LidarrStatus),
            "rootfolder" => Respond(HttpStatusCode.OK, RootFolders),
            "qualityprofile" => Respond(HttpStatusCode.OK, QualityProfiles),
            "metadataprofile" => Respond(HttpStatusCode.OK, MetadataProfiles),
            _ => Respond(HttpStatusCode.NotFound, string.Empty),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var uri = request.RequestUri!.ToString();
            var key = request.Headers.TryGetValues("X-Api-Key", out var values) ? values.Single() : null;
            lock (_requests)
            {
                _requests.Add(new SentRequest(uri, key));
            }

            var marker = uri.IndexOf("/api/v1/", StringComparison.Ordinal);
            return Task.FromResult(_respond(uri[(marker + "/api/v1/".Length)..]));
        }
    }
}
