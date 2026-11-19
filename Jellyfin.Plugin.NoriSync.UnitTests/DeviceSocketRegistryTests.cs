using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller.Net.WebSocketMessages.Outbound;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// The plugin's own register of device sockets (<see cref="DeviceSocketRegistry"/>), which playback
/// sync pushes and pauses through instead of Jellyfin's session list.
/// </summary>
public sealed class DeviceSocketRegistryTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static DeviceSocketRegistry NewRegistry() => new(NullLogger<DeviceSocketRegistry>.Instance);

    [Fact]
    public void ASocketIsFoundByItsUserAndDeviceUntilItCloses()
    {
        var registry = NewRegistry();
        var socket = new FakeSocket();
        registry.Register(socket, User, "Mac-Device");

        Assert.True(registry.HasTalkingSocket(User, "mac-device", Now));
        Assert.False(registry.HasTalkingSocket(Guid.NewGuid(), "mac-device", Now));
        Assert.False(registry.HasTalkingSocket(User, "phone", Now));

        socket.Close();
        Assert.False(registry.HasTalkingSocket(User, "mac-device", Now));
    }

    /// <summary>
    /// Jellyfin never closes a socket that stopped talking, and a dead connection stays open.
    /// </summary>
    [Fact]
    public void ASocketSilentForAMinuteCountsAsGone()
    {
        var registry = NewRegistry();
        var socket = new FakeSocket { LastActivityDate = Now.UtcDateTime };
        registry.Register(socket, User, "mac");

        Assert.True(registry.HasTalkingSocket(User, "mac", Now + DeviceSocketRegistry.SilenceLimit - TimeSpan.FromSeconds(1)));
        Assert.False(registry.HasTalkingSocket(User, "mac", Now + DeviceSocketRegistry.SilenceLimit));
        Assert.Empty(registry.NoriSockets(User, Now + DeviceSocketRegistry.SilenceLimit));
    }

    [Fact]
    public async Task APauseGoesOverEveryTalkingSocketOfThatDevice()
    {
        var registry = NewRegistry();
        var first = new FakeSocket();
        var second = new FakeSocket();
        var silent = new FakeSocket { LastActivityDate = DateTime.UtcNow - TimeSpan.FromMinutes(5) };
        var other = new FakeSocket();
        registry.Register(first, User, "mac");
        registry.Register(second, User, "mac");
        registry.Register(silent, User, "mac");
        registry.Register(other, User, "phone");

        Assert.Equal(2, await registry.SendPause(User, "mac", DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(PlaystateCommand.Pause, Assert.IsType<PlaystateMessage>(Assert.Single(first.Sent)).Data!.Command);
        Assert.Single(second.Sent);
        Assert.Empty(silent.Sent);
        Assert.Empty(other.Sent);
    }

    [Fact]
    public void OnlyTheUsersNoriSocketsArePushedTo()
    {
        var registry = NewRegistry();
        registry.Register(new FakeSocket(), User, "mac", "Nori");
        registry.Register(new FakeSocket(), User, "web", "Jellyfin Web");
        registry.Register(new FakeSocket(), Guid.NewGuid(), "kids", "Nori");

        Assert.Equal(["mac"], registry.NoriSockets(User, DateTimeOffset.UtcNow).Select(s => s.DeviceId));
    }

    [Fact]
    public void ADevicesFirstSocketAndLastCloseAreAnnounced()
    {
        var registry = NewRegistry();
        var events = new List<string>();
        registry.SocketsChanged += (_, e) => events.Add(e.DeviceId);
        var first = new FakeSocket();
        var second = new FakeSocket();

        registry.Register(first, User, "mac");
        registry.Register(second, User, "mac");
        first.Close();
        Assert.Equal(["mac"], events);

        second.Close();
        Assert.Equal(["mac", "mac"], events);
    }

    [Fact]
    public void AConnectionWithoutAUserOrDeviceIsNotKept()
    {
        var registry = NewRegistry();
        registry.Register(new FakeSocket(), Guid.Empty, "mac");
        registry.Register(new FakeSocket(), User, null);

        Assert.False(registry.HasTalkingSocket(Guid.Empty, "mac", Now));
        Assert.False(registry.HasTalkingSocket(User, string.Empty, Now));
    }
}
