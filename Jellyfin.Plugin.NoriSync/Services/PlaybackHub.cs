using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.Net.WebSocketMessages.Outbound;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Playback sync between a user's devices (API_FORMAT §3.20): applies reports, answers who is
/// where, and pushes every change of a device's record to the user's Nori sockets, so clients
/// never poll. Liveness — playing, and reachable — is decided here, and a change of it is pushed
/// too: when a playing device's socket opens, closes or falls silent.
/// </summary>
public sealed class PlaybackHub : IDisposable
{
    /// <summary>
    /// The <c>Arguments["NoriSync"]</c> of a pushed device record.
    /// </summary>
    public const string PushKind = "playback.device";

    /// <summary>
    /// A playing device that hasn't reported for this long isn't live: one 4-minute heartbeat and
    /// a minute's slack.
    /// </summary>
    public static readonly TimeSpan MaxLiveReportAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often a playing device's liveness is re-checked: a socket going silent changes it with
    /// no event.
    /// </summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PauseTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<PlaybackStore?> _store;
    private readonly DeviceSocketRegistry _sockets;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly object _trackLock = new();

    /// <summary>
    /// The playing devices, with the liveness last pushed for each.
    /// </summary>
    private readonly Dictionary<(Guid User, string Device), bool> _playing = new();
    private readonly ITimer? _sweep;
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackHub"/> class.
    /// </summary>
    /// <param name="store">The store, once the plugin has opened it.</param>
    /// <param name="sockets">The socket registry.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="sweeps">Whether liveness is re-checked on a timer (off in tests, which call <see cref="Sweep"/>).</param>
    public PlaybackHub(Func<PlaybackStore?> store, DeviceSocketRegistry sockets, ILogger logger, TimeProvider clock, bool sweeps = true)
    {
        ArgumentNullException.ThrowIfNull(sockets);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _sockets = sockets;
        _logger = logger;
        _clock = clock;
        _sockets.SocketsChanged += OnSocketsChanged;
        if (sweeps)
        {
            _sweep = clock.CreateTimer(_ => SweepFromTimer(), null, SweepInterval, SweepInterval);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the store is open.
    /// </summary>
    public bool IsAvailable => _store() != null;

    /// <summary>
    /// Applies a device's report and pushes what it changed. A take-over's source is paused
    /// afterwards, on the hub's own timeout: never cancelled with the reporting request.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The reporting device.</param>
    /// <param name="deviceName">Its name, if it gave one.</param>
    /// <param name="report">The report.</param>
    /// <returns>What it did, and the pushes and pause it started (awaited only by tests).</returns>
    public (PlaybackApplyOutcome Outcome, Task Delivery) Report(Guid userId, string deviceId, string? deviceName, PlaybackReport report)
    {
        var store = _store() ?? throw new InvalidOperationException("The playback store isn't open.");
        EnsureLoaded(store);
        var now = _clock.GetUtcNow();
        var outcome = store.ApplyReport(userId, deviceId, deviceName, report, now);
        if (outcome.Stale)
        {
            return (outcome, Task.CompletedTask);
        }

        var pushes = outcome.Changed.Select(device => Track(device, now)).ToList();
        var delivery = Task.Run(async () =>
        {
            foreach (var (device, live) in pushes)
            {
                await Push(device, live, now).ConfigureAwait(false);
            }

            if (outcome.TookOverFrom is { } source)
            {
                using var timeout = new CancellationTokenSource(PauseTimeout);
                var sent = await _sockets.SendPause(userId, source, now, timeout.Token).ConfigureAwait(false);
                _logger.LogInformation(
                    "Playback taken over from {Source} by {Device}; pause sent over {Count} socket(s)", source, deviceId, sent);
            }
        });
        return (outcome, delivery);
    }

    /// <summary>
    /// Clears a device's session if it still holds it (it stopped), and pushes the change.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="sessionId">The session it stopped.</param>
    /// <returns>The push started (awaited only by tests).</returns>
    public Task Stop(Guid userId, string deviceId, string sessionId)
    {
        var store = _store() ?? throw new InvalidOperationException("The playback store isn't open.");
        EnsureLoaded(store);
        var now = _clock.GetUtcNow();
        if (store.ClearSession(userId, deviceId, sessionId, now) is not { } cleared)
        {
            return Task.CompletedTask;
        }

        var (device, live) = Track(cleared, now);
        return Task.Run(() => Push(device, live, now));
    }

    /// <summary>
    /// Every device of the user, as <paramref name="callerDevice"/> sees them.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="callerDevice">The asking device.</param>
    /// <returns>The answer.</returns>
    public PlaybackDevicesResponse Devices(Guid userId, string callerDevice)
    {
        var store = _store() ?? throw new InvalidOperationException("The playback store isn't open.");
        EnsureLoaded(store);
        var now = _clock.GetUtcNow();
        var devices = store.GetDevices(userId);
        return new PlaybackDevicesResponse
        {
            ServerNow = now.ToUnixTimeMilliseconds(),
            Version = devices.Count == 0 ? 0 : devices.Max(d => d.Version),
            Devices = devices
                .Select(d => PlaybackDeviceDto.From(d, SameDevice(d.DeviceId, callerDevice), IsLive(d, now)))
                .ToList()
        };
    }

    /// <summary>
    /// The queue a session holds at a revision.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="queueRev">The revision.</param>
    /// <returns>The queue's JSON, or <see langword="null"/>.</returns>
    public string? Queue(Guid userId, string sessionId, int queueRev) =>
        (_store() ?? throw new InvalidOperationException("The playback store isn't open.")).GetQueue(userId, sessionId, queueRev);

    /// <summary>
    /// Re-checks every playing device's liveness and pushes the ones that changed. On a timer;
    /// public for tests.
    /// </summary>
    /// <returns>The pushes started.</returns>
    public Task Sweep()
    {
        var store = _store();
        if (store == null)
        {
            return Task.CompletedTask;
        }

        EnsureLoaded(store);
        List<(Guid User, string Device)> playing;
        lock (_trackLock)
        {
            playing = _playing.Keys.ToList();
        }

        var pushes = new List<Task>();
        foreach (var (user, device) in playing)
        {
            if (Recheck(store, user, device) is { } push)
            {
                pushes.Add(push);
            }
        }

        return Task.WhenAll(pushes);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sockets.SocketsChanged -= OnSocketsChanged;
        _sweep?.Dispose();
    }

    /// <summary>
    /// Playing, reported recently, and holding a socket that talks: a pause sent now reaches it.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="now">The server's clock.</param>
    /// <returns><see langword="true"/> if it is live.</returns>
    internal bool IsLive(PlaybackDevice device, DateTimeOffset now) =>
        device.Status == PlaybackStatus.Playing
        && now.ToUnixTimeMilliseconds() - device.PositionAt <= (long)MaxLiveReportAge.TotalMilliseconds
        && _sockets.HasTalkingSocket(device.UserId, device.DeviceId, now);

    /// <summary>
    /// The push message for <paramref name="device"/>, as the device on the receiving socket sees it.
    /// </summary>
    /// <param name="device">The record.</param>
    /// <param name="live">Its liveness.</param>
    /// <param name="recipientDevice">The device the socket belongs to.</param>
    /// <param name="now">The server's clock.</param>
    /// <returns>The message.</returns>
    internal static GeneralCommandMessage PushMessage(PlaybackDevice device, bool live, string recipientDevice, DateTimeOffset now)
    {
        var dto = PlaybackDeviceDto.From(device, SameDevice(device.DeviceId, recipientDevice), live);
        var command = new GeneralCommand(new Dictionary<string, string>
        {
            ["NoriSync"] = PushKind,
            ["Payload"] = JsonSerializer.Serialize(dto),
            ["ServerNow"] = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        })
        {
            Name = GeneralCommandType.SendString,
            ControllingUserId = device.UserId
        };
        return new GeneralCommandMessage(command);
    }

    private static bool SameDevice(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records whether <paramref name="device"/> is playing (so its liveness is watched) and the
    /// liveness about to be pushed.
    /// </summary>
    private (PlaybackDevice Device, bool Live) Track(PlaybackDevice device, DateTimeOffset now)
    {
        var live = IsLive(device, now);
        var key = (device.UserId, device.DeviceId.ToUpperInvariant());
        lock (_trackLock)
        {
            if (device.Status == PlaybackStatus.Playing)
            {
                _playing[key] = live;
            }
            else
            {
                _playing.Remove(key);
            }
        }

        return (device, live);
    }

    /// <summary>
    /// Pushes <paramref name="deviceId"/>'s record if its liveness moved since the last push.
    /// </summary>
    private Task? Recheck(PlaybackStore store, Guid userId, string deviceId)
    {
        var key = (userId, deviceId.ToUpperInvariant());

        // Only a watched (playing) device's liveness can change without a report. Checking first
        // also keeps every other socket's opening and closing — any client's — off the database.
        lock (_trackLock)
        {
            if (!_playing.ContainsKey(key))
            {
                return null;
            }
        }

        var now = _clock.GetUtcNow();
        var device = store.GetDevice(userId, deviceId);
        bool? pushed;
        bool live;
        lock (_trackLock)
        {
            if (!_playing.TryGetValue(key, out var last))
            {
                return null;
            }

            pushed = last;
            live = device != null && IsLive(device, now);
            if (device == null || device.Status != PlaybackStatus.Playing)
            {
                _playing.Remove(key);
            }
            else
            {
                _playing[key] = live;
            }
        }

        if (device == null || live == pushed)
        {
            return null;
        }

        return Task.Run(() => Push(device, live, now));
    }

    // Raised on Jellyfin's own WebSocket connect and close paths, for every client: a throw here
    // would fail that client's socket, so nothing may escape.
    private void OnSocketsChanged(object? sender, DeviceSocketsChangedEventArgs e)
    {
        try
        {
            if (_store() is { } store)
            {
                _ = Recheck(store, e.UserId, e.DeviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not re-check playback liveness for device {Device}", e.DeviceId);
        }
    }

    /// <summary>
    /// The timer's sweep. An exception escaping a timer callback ends the process (as when the
    /// store is closed at shutdown while the timer still runs), so the sweep's are logged instead.
    /// </summary>
    internal void SweepFromTimer()
    {
        try
        {
            _ = Sweep();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playback liveness sweep failed");
        }
    }

    /// <summary>
    /// After a restart, the devices playing when the server stopped are watched again.
    /// </summary>
    private void EnsureLoaded(PlaybackStore store)
    {
        lock (_trackLock)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            var now = _clock.GetUtcNow();
            foreach (var device in store.GetPlayingDevices(now - MaxLiveReportAge))
            {
                _playing[(device.UserId, device.DeviceId.ToUpperInvariant())] = false;
            }
        }
    }

    private async Task Push(PlaybackDevice device, bool live, DateTimeOffset now)
    {
        foreach (var (recipient, socket) in _sockets.NoriSockets(device.UserId, now))
        {
            using var timeout = new CancellationTokenSource(PushTimeout);
            try
            {
                await socket.SendAsync(PushMessage(device, live, recipient, now), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Could not push a playback change to device {Device}", recipient);
            }
        }
    }
}
