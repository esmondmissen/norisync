using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller.Net.WebSocketMessages.Outbound;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Pushes, liveness and the take-over's pause (<see cref="PlaybackHub"/>).
/// </summary>
public sealed class PlaybackHubTests : IDisposable
{
    private static readonly Guid User = Guid.NewGuid();

    private readonly string _tempDir;
    private readonly PlaybackStore _store;
    private readonly DeviceSocketRegistry _sockets = new(NullLogger<DeviceSocketRegistry>.Instance);
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly PlaybackHub _hub;

    public PlaybackHubTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-hub-tests-" + Guid.NewGuid().ToString("N"));
        _store = new PlaybackStore(_tempDir, NullLogger.Instance);
        _hub = new PlaybackHub(() => _store, _sockets, NullLogger.Instance, _clock, sweeps: false);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _store.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private FakeSocket Connect(string device, string client = "Nori")
    {
        var socket = new FakeSocket { LastActivityDate = _clock.Now.UtcDateTime };
        _sockets.Register(socket, User, device, client);
        return socket;
    }

    private static PlaybackDeviceDto[] Pushed(FakeSocket socket)
    {
        lock (socket.Sent)
        {
            return socket.Sent.OfType<GeneralCommandMessage>()
                .Where(m => m.Data!.Name == GeneralCommandType.SendString && m.Data.Arguments["NoriSync"] == PlaybackHub.PushKind)
                .Select(m => JsonSerializer.Deserialize<PlaybackDeviceDto>(m.Data!.Arguments["Payload"])!)
                .ToArray();
        }
    }

    private async Task<PlaybackApplyOutcome> Report(string device, PlaybackReport report)
    {
        var (outcome, delivery) = _hub.Report(User, device, device, report);
        await delivery;
        return outcome;
    }

    [Fact]
    public async Task AReportIsPushedToEveryNoriSocketOfTheUserAsEachSeesIt()
    {
        var mac = Connect("mac");
        var phone = Connect("phone");
        var web = Connect("web", "Jellyfin Web");

        await Report("mac", Reports.Make(1, ticks: 70));

        var toPhone = Assert.Single(Pushed(phone));
        Assert.Equal(("mac", false, true, "playing", 70L), (toPhone.DeviceId, toPhone.IsCaller, toPhone.Live, toPhone.Status, toPhone.PositionTicks));
        Assert.True(Assert.Single(Pushed(mac)).IsCaller);
        Assert.Empty(web.Sent);
    }

    [Fact]
    public async Task AStaleReportPushesNothing()
    {
        var phone = Connect("phone");
        await Report("mac", Reports.Make(5));
        await Report("mac", Reports.Make(4));
        Assert.Single(Pushed(phone));
    }

    [Fact]
    public async Task ATakeOverPushesBothDevicesAndPausesTheSource()
    {
        var mac = Connect("mac");
        var phone = Connect("phone");
        await Report("mac", Reports.Make(1, session: "x"));

        var outcome = await Report("phone", Reports.Make(1, session: "x", queue: null, takeOver: "x"));

        Assert.Equal("mac", outcome.TookOverFrom);
        lock (mac.Sent)
        {
            Assert.Equal(PlaystateCommand.Pause, mac.Sent.OfType<PlaystateMessage>().Single().Data!.Command);
        }

        Assert.Empty(phone.Sent.OfType<PlaystateMessage>());
        var macSees = Pushed(mac);
        Assert.Contains(macSees, d => d.DeviceId == "mac" && d.IsCaller && d.MovedTo?.DeviceId == "phone" && d.SessionId == null);
        Assert.Contains(macSees, d => d.DeviceId == "phone" && d.SessionId == "x" && d.Live);
    }

    [Fact]
    public async Task APlayingDeviceWhoseSocketFallsSilentIsPushedNotLive()
    {
        var mac = Connect("mac");
        var phone = Connect("phone");
        await Report("mac", Reports.Make(1));
        Assert.True(Pushed(phone).Last().Live);

        _clock.Now += TimeSpan.FromSeconds(30);
        phone.LastActivityDate = _clock.Now.UtcDateTime;
        await _hub.Sweep();
        Assert.Single(Pushed(phone));

        _clock.Now += DeviceSocketRegistry.SilenceLimit;
        phone.LastActivityDate = _clock.Now.UtcDateTime;
        await _hub.Sweep();
        Assert.False(Pushed(phone).Last().Live);
        Assert.Equal(2, Pushed(phone).Length);

        mac.LastActivityDate = _clock.Now.UtcDateTime;
        await _hub.Sweep();
        Assert.True(Pushed(phone).Last().Live);
    }

    [Fact]
    public async Task APlayingDeviceWhoseLastSocketClosesIsPushedNotLiveAtOnce()
    {
        var mac = Connect("mac");
        var phone = Connect("phone");
        await Report("mac", Reports.Make(1));

        mac.Close();
        await Task.Delay(200);
        Assert.False(Pushed(phone).Last().Live);
    }

    [Fact]
    public async Task APausedDeviceIsNeitherLiveNorWatched()
    {
        Connect("mac");
        var phone = Connect("phone");
        await Report("mac", Reports.Make(1, status: PlaybackStatus.Paused, kind: PlaybackEvent.Progress));
        Assert.False(Pushed(phone).Last().Live);

        _clock.Now += TimeSpan.FromMinutes(2);
        await _hub.Sweep();
        Assert.Single(Pushed(phone));
    }

    [Fact]
    public async Task AStopIsPushed()
    {
        var phone = Connect("phone");
        await Report("mac", Reports.Make(1, session: "x"));
        await _hub.Stop(User, "mac", "x");
        Assert.Equal(("stopped", (string?)null), (Pushed(phone).Last().Status, Pushed(phone).Last().SessionId));
    }

    [Fact]
    public async Task DevicesAreAnsweredWithTheCallerMarkedAndLivenessDecided()
    {
        Connect("mac");
        await Report("mac", Reports.Make(1));
        await Report("phone", Reports.Make(1, session: "y", status: PlaybackStatus.Paused, kind: PlaybackEvent.Progress));

        var answer = _hub.Devices(User, "phone");
        Assert.Equal(_clock.Now.ToUnixTimeMilliseconds(), answer.ServerNow);
        var mac = answer.Devices.Single(d => d.DeviceId == "mac");
        var phone = answer.Devices.Single(d => d.DeviceId == "phone");
        Assert.Equal((false, true), (mac.IsCaller, mac.Live));
        Assert.Equal((true, false), (phone.IsCaller, phone.Live));
        Assert.Equal(answer.Devices.Max(d => d.Version), answer.Version);
    }

    [Fact]
    public async Task APlayingDeviceIsNotLiveOnceItsLastReportIsTooOld()
    {
        var mac = Connect("mac");
        await Report("mac", Reports.Make(1));
        _clock.Now += PlaybackHub.MaxLiveReportAge + TimeSpan.FromSeconds(1);
        mac.LastActivityDate = _clock.Now.UtcDateTime;
        Assert.False(_hub.Devices(User, "phone").Devices.Single().Live);
    }

    /// <summary>
    /// Socket events run on Jellyfin's connect and close paths for every client: with the store
    /// unreadable, a watched device's socket still opens and closes without a throw.
    /// </summary>
    [Fact]
    public async Task SocketEventsSurviveAStoreThatCannotBeRead()
    {
        await Report("mac", Reports.Make(1));
        _store.Dispose();

        var mac = Connect("mac");
        mac.Close();
        Connect("web", "Jellyfin Web").Close();
    }

    /// <summary>
    /// An exception out of a timer callback would end the server, so the timer's sweep logs it.
    /// </summary>
    [Fact]
    public async Task TheTimersSweepSurvivesAStoreThatCannotBeRead()
    {
        await Report("mac", Reports.Make(1));
        _store.Dispose();

        Assert.ThrowsAny<Exception>(() => { _ = _hub.Sweep(); });
        _hub.SweepFromTimer();
    }

    /// <summary>
    /// The push is a standard Jellyfin message any client parses: a <c>GeneralCommand</c>.
    /// </summary>
    [Fact]
    public void APushIsAGeneralCommandCarryingTheRecord()
    {
        var device = new PlaybackDevice(User, "mac", "MacBook Pro", "x", 3, true, 2, "ab", 10, 1, PlaybackStatus.Playing, 1, 7, 1,
            null, null, null);
        var message = PlaybackHub.PushMessage(device, live: true, recipientDevice: "phone", DateTimeOffset.FromUnixTimeMilliseconds(42));

        Assert.Equal(SessionMessageType.GeneralCommand, message.MessageType);
        Assert.Equal(GeneralCommandType.SendString, message.Data!.Name);
        Assert.Equal("42", message.Data.Arguments["ServerNow"]);
        var payload = JsonDocument.Parse(message.Data.Arguments["Payload"]).RootElement;
        Assert.Equal("mac", payload.GetProperty("DeviceId").GetString());
        Assert.Equal("playing", payload.GetProperty("Status").GetString());
        Assert.Equal(3, payload.GetProperty("QueueRev").GetInt32());
        Assert.True(payload.GetProperty("Live").GetBoolean());
        Assert.False(payload.GetProperty("IsCaller").GetBoolean());
    }
}
