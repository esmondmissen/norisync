using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// The configuration form's SoulSync check: its own API with an admin key (a read key passes the
/// status call but may not list profiles), then its Lidarr-compatible API for albums. Answers as
/// SoulSync's <c>api/auth.py</c> and <c>core/lidarr_compat/resources.py</c> give them.
/// </summary>
public sealed class SoulSyncProbeTests
{
    private const string Status = """{ "uptime": "1h", "uptime_seconds": 3600, "services": { "soulseek": true } }""";

    private const string Profiles = """{ "profiles": [ { "id": 1, "name": "Admin", "is_admin": true }, { "id": 2, "name": "Family" } ] }""";

    private const string CompatStatus = """{ "appName": "Lidarr", "instanceName": "SoulSync", "version": "2.9.6", "urlBase": "/lidarr" }""";

    /// <summary>
    /// A healthy SoulSync with its compat API on: songs and albums, and its profiles.
    /// </summary>
    [Fact]
    public async Task Probe_HealthyWithCompat_ConnectsSongsAndAlbums()
    {
        var fake = Healthy();
        fake.On("GET lidarr/system/status", HttpStatusCode.OK, CompatStatus);
        fake.On("GET lidarr/rootfolder", HttpStatusCode.OK, """[ { "id": 1, "path": "/app/Transfer" } ]""");
        fake.On("GET lidarr/qualityprofile", HttpStatusCode.OK, """[ { "id": 1, "name": "Default" } ]""");
        fake.On("GET lidarr/metadataprofile", HttpStatusCode.OK, """[ { "id": 1, "name": "Standard" } ]""");

        var result = await Probe(fake);

        Assert.Equal(SoulSyncProbeStatus.Connected, result.Status);
        Assert.True(result.AlbumsAvailable);
        Assert.Null(result.AlbumsMessage);
        Assert.Equal(new[] { "1", "2" }, System.Linq.Enumerable.Select(result.Profiles, p => p.Value));
        Assert.Equal("Family", result.Profiles[1].Label);
        Assert.Equal("sk_test", fake.LastApiKey);
    }

    /// <summary>
    /// The compat API is off by default and answers 404: songs still work, albums say how to
    /// turn it on.
    /// </summary>
    [Fact]
    public async Task Probe_CompatOff_SongsOnlyWithAHint()
    {
        var result = await Probe(Healthy());

        Assert.Equal(SoulSyncProbeStatus.Connected, result.Status);
        Assert.False(result.AlbumsAvailable);
        Assert.Contains("Lidarr-compatible API", result.AlbumsMessage, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A read key passes the status call; listing profiles shows it can't add anything.
    /// </summary>
    [Fact]
    public async Task Probe_ReadOnlyKey_IsRefused()
    {
        var fake = new FakeSoulSync();
        fake.On("GET system/status", HttpStatusCode.OK, FakeSoulSync.Ok(Status));
        fake.On("GET profiles", HttpStatusCode.Forbidden, FakeSoulSync.Error("INSUFFICIENT_SCOPE", "This API key is read-only."));

        var result = await Probe(fake);

        Assert.Equal(SoulSyncProbeStatus.ReadOnlyKey, result.Status);
        Assert.Empty(fake.Requests("GET", "lidarr/system/status"));
    }

    /// <summary>
    /// A missing or wrong key.
    /// </summary>
    /// <param name="status">SoulSync's status.</param>
    /// <param name="code">Its error code.</param>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AUTH_REQUIRED")]
    [InlineData(HttpStatusCode.Forbidden, "INVALID_KEY")]
    public async Task Probe_RejectedKey_IsUnauthorized(HttpStatusCode status, string code)
    {
        var fake = new FakeSoulSync();
        fake.On("GET system/status", status, FakeSoulSync.Error(code, "nope"));

        Assert.Equal(SoulSyncProbeStatus.Unauthorized, (await Probe(fake)).Status);
    }

    /// <summary>
    /// A page that isn't SoulSync's envelope (a proxy's login page, another app) isn't SoulSync.
    /// </summary>
    [Fact]
    public async Task Probe_NotTheEnvelope_IsNotSoulSync()
    {
        var fake = new FakeSoulSync();
        fake.On("GET system/status", HttpStatusCode.OK, "<html>login</html>");

        Assert.Equal(SoulSyncProbeStatus.NotSoulSync, (await Probe(fake)).Status);
    }

    /// <summary>
    /// A host that doesn't answer.
    /// </summary>
    [Fact]
    public async Task Probe_Unreachable()
    {
        var fake = new FakeSoulSync { Throw = new HttpRequestException("connection refused") };

        Assert.Equal(SoulSyncProbeStatus.Unreachable, (await Probe(fake)).Status);
    }

    /// <summary>
    /// The form's own checks, before anything is sent.
    /// </summary>
    [Fact]
    public async Task Probe_BadInput_IsCaughtBeforeSending()
    {
        var fake = new FakeSoulSync();
        using var client = FakeSoulSync.Client(fake);
        using var compat = FakeSoulSync.CompatClient(fake);

        Assert.Equal(SoulSyncProbeStatus.InvalidUrl, (await client.ProbeAsync("soulsync:8008", "sk_x", compat, CancellationToken.None)).Status);
        Assert.Equal(SoulSyncProbeStatus.MissingApiKey, (await client.ProbeAsync("http://soulsync:8008", " ", compat, CancellationToken.None)).Status);
        Assert.Empty(fake.All());
    }

    private static FakeSoulSync Healthy()
    {
        var fake = new FakeSoulSync();
        fake.On("GET system/status", HttpStatusCode.OK, FakeSoulSync.Ok(Status));
        fake.On("GET profiles", HttpStatusCode.OK, FakeSoulSync.Ok(Profiles));
        return fake;
    }

    private static async Task<SoulSyncProbeResponse> Probe(FakeSoulSync fake)
    {
        using var client = FakeSoulSync.Client(fake);
        using var compat = FakeSoulSync.CompatClient(fake);
        return await client.ProbeAsync(FakeSoulSync.BaseUri.ToString(), "sk_test", compat, CancellationToken.None);
    }
}
