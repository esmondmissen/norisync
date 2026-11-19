using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Net.WebSocketMessages;
using MediaBrowser.Controller.Net.WebSocketMessages.Outbound;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Every open Jellyfin WebSocket, by user and device, as the connections themselves report
/// them — not through <c>ISessionManager.Sessions</c>. A socket that reconnects while Jellyfin
/// is ending its device's session (the old socket's close ends the session asynchronously) is
/// attached to the session being disposed, which is no longer listed: looked up through the
/// sessions, the device reads as having no socket, and a transfer's pause can't reach it.
/// </summary>
public sealed class DeviceSocketRegistry : IWebSocketListener
{
    /// <summary>
    /// The <c>Client</c> every Nori sends: only Nori sockets are pushed playback changes.
    /// </summary>
    public const string NoriClient = "Nori";

    /// <summary>
    /// A socket that has sent nothing for this long counts as gone. Jellyfin never closes a socket
    /// that stopped sending keep-alives (after 60 s it only stops watching it), and a dead TCP
    /// connection stays <see cref="WebSocketState.Open"/> until a write to it fails.
    /// </summary>
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private readonly Dictionary<(Guid User, string Device), List<Entry>> _sockets = new();
    private readonly ILogger<DeviceSocketRegistry> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceSocketRegistry"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public DeviceSocketRegistry(ILogger<DeviceSocketRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// A device's first socket opened or its last one closed: its liveness may have changed.
    /// Raised outside the registry's lock.
    /// </summary>
    public event EventHandler<DeviceSocketsChangedEventArgs>? SocketsChanged;

    /// <inheritdoc />
    public Task ProcessMessageAsync(WebSocketMessageInfo message) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ProcessWebSocketConnectedAsync(IWebSocketConnection connection, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var auth = connection.AuthorizationInfo;
        if (auth != null)
        {
            Register(connection, auth.UserId, auth.DeviceId, auth.Client);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Keeps <paramref name="connection"/> as the user's <paramref name="deviceId"/> socket until it
    /// closes. A connection with no user or device is not kept.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="userId">Its user.</param>
    /// <param name="deviceId">Its device.</param>
    /// <param name="client">Its client (<c>Nori</c>, <c>Jellyfin Web</c>…).</param>
    internal void Register(IWebSocketConnection connection, Guid userId, string? deviceId, string? client = NoriClient)
    {
        if (userId == Guid.Empty || string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        var key = Key(userId, deviceId);
        bool first;
        lock (_lock)
        {
            if (!_sockets.TryGetValue(key, out var list))
            {
                list = new List<Entry>();
                _sockets[key] = list;
            }

            list.RemoveAll(e => e.Connection.State != WebSocketState.Open);
            first = list.Count == 0;
            list.Add(new Entry(connection, deviceId, client));
        }

        connection.Closed += (_, _) => Remove(key, connection);
        if (first)
        {
            SocketsChanged?.Invoke(this, new DeviceSocketsChangedEventArgs(userId, deviceId));
        }
    }

    /// <summary>
    /// Whether the user's <paramref name="deviceId"/> holds a socket that has talked within
    /// <see cref="SilenceLimit"/>.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="now">The server's clock.</param>
    /// <returns><see langword="true"/> if a message could reach it.</returns>
    public bool HasTalkingSocket(Guid userId, string deviceId, DateTimeOffset now) =>
        Talking(userId, deviceId, now).Count > 0;

    /// <summary>
    /// The user's Nori sockets that have talked within <see cref="SilenceLimit"/>, with the device
    /// each belongs to.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="now">The server's clock.</param>
    /// <returns>The sockets.</returns>
    public IReadOnlyList<(string DeviceId, IWebSocketConnection Connection)> NoriSockets(Guid userId, DateTimeOffset now)
    {
        lock (_lock)
        {
            return _sockets
                .Where(pair => pair.Key.User == userId)
                .SelectMany(pair => pair.Value)
                .Where(e => string.Equals(e.Client, NoriClient, StringComparison.Ordinal) && IsTalking(e.Connection, now))
                .Select(e => (e.DeviceId, e.Connection))
                .ToList();
        }
    }

    /// <summary>
    /// Sends a <c>Playstate</c> pause over every talking socket of the user's <paramref name="deviceId"/>.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="now">The server's clock.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many sockets it was sent over.</returns>
    public async Task<int> SendPause(Guid userId, string deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var socket in Talking(userId, deviceId, now))
        {
            var request = new PlaystateRequest { Command = PlaystateCommand.Pause, ControllingUserId = userId.ToString("N") };
            try
            {
                await socket.SendAsync(new PlaystateMessage(request), cancellationToken).ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogInformation(ex, "Could not send the transfer pause over a device socket");
            }
        }

        return sent;
    }

    private static bool IsTalking(IWebSocketConnection connection, DateTimeOffset now) =>
        connection.State == WebSocketState.Open
        && now.UtcDateTime - connection.LastActivityDate < SilenceLimit;

    // Jellyfin compares device ids case-insensitively.
    private static (Guid User, string Device) Key(Guid userId, string deviceId) => (userId, deviceId.ToUpperInvariant());

    private List<IWebSocketConnection> Talking(Guid userId, string deviceId, DateTimeOffset now)
    {
        lock (_lock)
        {
            return _sockets.TryGetValue(Key(userId, deviceId), out var list)
                ? list.Where(e => IsTalking(e.Connection, now)).Select(e => e.Connection).ToList()
                : new List<IWebSocketConnection>();
        }
    }

    private void Remove((Guid User, string Device) key, IWebSocketConnection connection)
    {
        bool last;
        string? deviceId = null;
        lock (_lock)
        {
            if (!_sockets.TryGetValue(key, out var list))
            {
                return;
            }

            var entry = list.Find(e => ReferenceEquals(e.Connection, connection));
            if (entry == null)
            {
                return;
            }

            list.Remove(entry);
            deviceId = entry.DeviceId;
            last = list.Count == 0;
            if (last)
            {
                _sockets.Remove(key);
            }
        }

        if (last)
        {
            SocketsChanged?.Invoke(this, new DeviceSocketsChangedEventArgs(key.User, deviceId));
        }
    }

    private sealed record Entry(IWebSocketConnection Connection, string DeviceId, string? Client);
}
