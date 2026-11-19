using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Net.WebSocketMessages;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// A clock the tests move by hand.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    public ManualClock(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A Jellyfin socket that records what is sent over it.
/// </summary>
internal sealed class FakeSocket : IWebSocketConnection
{
    public event EventHandler<EventArgs>? Closed;

    public List<object> Sent { get; } = new();

    public DateTime LastActivityDate { get; set; } = DateTime.UtcNow;

    public DateTime LastKeepAliveDate { get; set; }

    public Func<WebSocketMessageInfo, Task>? OnReceive { get; set; }

    public WebSocketState State { get; private set; } = WebSocketState.Open;

    public AuthorizationInfo AuthorizationInfo { get; } = new();

    public IPAddress? RemoteEndPoint => null;

    public void Close()
    {
        State = WebSocketState.Closed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public Task SendAsync(OutboundWebSocketMessage message, CancellationToken cancellationToken)
    {
        lock (Sent)
        {
            Sent.Add(message);
        }

        return Task.CompletedTask;
    }

    public Task SendAsync<T>(OutboundWebSocketMessage<T> message, CancellationToken cancellationToken)
    {
        lock (Sent)
        {
            Sent.Add(message);
        }

        return Task.CompletedTask;
    }

    public Task ReceiveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Reports as a client sends them.
/// </summary>
internal static class Reports
{
    public static readonly Guid Item = Guid.Parse("4f2a9c1e-0000-4000-8000-0000000000ab");

    public static PlaybackReport Make(
        long seq,
        string session = "s1",
        int rev = 1,
        string? queue = "{\"trackIds\":[\"a\"]}",
        PlaybackEvent kind = PlaybackEvent.Start,
        PlaybackStatus status = PlaybackStatus.Playing,
        int entry = 0,
        long ticks = 0,
        bool shareable = true,
        string? takeOver = null) =>
        new(seq, kind, session, rev, shareable, queue, entry, Item, ticks, status, null, PlayMethod.DirectPlay, takeOver);
}
