using System.Text.Json;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// The report body (API_FORMAT §3.20) read into a <see cref="PlaybackReport"/>.
/// </summary>
public sealed class PlaybackReportRequestTests
{
    private static PlaybackReportRequest Valid() => new()
    {
        Seq = 1794000000000,
        Event = "Start",
        SessionId = "3f9c-a_1",
        QueueRev = 2,
        EntryIndex = 4,
        ItemId = Reports.Item.ToString("N"),
        PositionTicks = 5,
        Status = "playing",
        PlaySessionId = "abc",
        PlayMethod = "DirectPlay",
        Queue = JsonDocument.Parse("{\"trackIds\":[\"a\",\"b\"]}").RootElement
    };

    [Fact]
    public void AReportCarriesItsSessionAndWhatJellyfinsBodiesCarry()
    {
        Assert.True(PlaybackReportRequest.TryParse(Valid(), out var report, out _));
        Assert.Equal(
            new PlaybackReport(1794000000000, PlaybackEvent.Start, "3f9c-a_1", 2, true, "{\"trackIds\":[\"a\",\"b\"]}", 4,
                Reports.Item, 5, PlaybackStatus.Playing, "abc", PlayMethod.DirectPlay, null),
            report);
    }

    [Theory]
    [InlineData("Start", PlaybackEvent.Start)]
    [InlineData("Progress", PlaybackEvent.Progress)]
    [InlineData("Stopped", PlaybackEvent.Stopped)]
    [InlineData("Cursor", PlaybackEvent.Cursor)]
    public void EveryEventIsRead(string sent, PlaybackEvent expected)
    {
        var request = Valid();
        request.Event = sent;
        Assert.True(PlaybackReportRequest.TryParse(request, out var report, out _));
        Assert.Equal(expected, report!.Event);
    }

    [Fact]
    public void NoQueueAndNoShareableAreAllowed()
    {
        var request = Valid();
        request.Queue = null;
        Assert.True(PlaybackReportRequest.TryParse(request, out var report, out _));
        Assert.Null(report!.Queue);
        Assert.True(report.Shareable);

        request.Shareable = false;
        Assert.True(PlaybackReportRequest.TryParse(request, out report, out _));
        Assert.False(report!.Shareable);
    }

    public static TheoryData<string, object?> Refused => new()
    {
        { "Seq", 0L },
        { "Event", "Paused" },
        { "SessionId", "" },
        { "SessionId", "has space" },
        { "SessionId", new string('a', PlaybackReportRequest.MaxSessionIdLength + 1) },
        { "QueueRev", -1 },
        { "EntryIndex", -1 },
        { "PositionTicks", -1L },
        { "ItemId", "not-an-id" },
        { "ItemId", "00000000000000000000000000000000" },
        { "Status", "buffering" },
        { "PlayMethod", "Direct" },
        { "PlaySessionId", new string('x', PlaybackReportRequest.MaxPlaySessionIdLength + 1) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void AReportThatIsntOneIsRefused(string field, object? value)
    {
        var request = Valid();
        typeof(PlaybackReportRequest).GetProperty(field)!.SetValue(request, value);
        Assert.False(PlaybackReportRequest.TryParse(request, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void AQueueMustBeAnObjectWithinTheCap()
    {
        var request = Valid();
        request.Queue = JsonDocument.Parse("[1,2]").RootElement;
        Assert.False(PlaybackReportRequest.TryParse(request, out _, out _));

        request.Queue = JsonDocument.Parse("{\"x\":\"" + new string('a', PlaybackReportRequest.MaxQueueLength) + "\"}").RootElement;
        Assert.False(PlaybackReportRequest.TryParse(request, out _, out _));
    }

    [Fact]
    public void ATakeOverGoesOnlyWithAPlayingStart()
    {
        var request = Valid();
        request.TakeOver = new PlaybackTakeOverRequest { SessionId = "other" };
        Assert.True(PlaybackReportRequest.TryParse(request, out var report, out _));
        Assert.Equal("other", report!.TakeOverSessionId);

        request.Status = "paused";
        Assert.False(PlaybackReportRequest.TryParse(request, out _, out _));

        request.Status = "playing";
        request.Event = "Progress";
        Assert.False(PlaybackReportRequest.TryParse(request, out _, out _));

        request.Event = "Start";
        request.TakeOver = new PlaybackTakeOverRequest();
        Assert.False(PlaybackReportRequest.TryParse(request, out _, out _));
    }

    [Theory]
    [InlineData(null, PlayMethod.Transcode)]
    [InlineData("DirectStream", PlayMethod.DirectStream)]
    public void APlayMethodIsPassedOnAndNoneIsATranscodeAsToJellyfin(string? sent, PlayMethod expected)
    {
        var request = Valid();
        request.PlayMethod = sent;
        Assert.True(PlaybackReportRequest.TryParse(request, out var report, out _));
        Assert.Equal(expected, report!.PlayMethod);
    }

    /// <summary>
    /// As Jellyfin's own playstate endpoints: a <c>Transcode</c> with no transcoding job behind its
    /// play session reaches Jellyfin as direct play, so the dashboard doesn't show one.
    /// </summary>
    [Theory]
    [InlineData(PlayMethod.Transcode, null, false, PlayMethod.DirectPlay)]
    [InlineData(PlayMethod.Transcode, "ps", false, PlayMethod.DirectPlay)]
    [InlineData(PlayMethod.Transcode, "ps", true, PlayMethod.Transcode)]
    [InlineData(PlayMethod.DirectStream, null, false, PlayMethod.DirectStream)]
    [InlineData(PlayMethod.DirectPlay, "ps", true, PlayMethod.DirectPlay)]
    public void ATranscodeWithNoTranscodingJobIsPassedOnAsDirectPlay(PlayMethod sent, string? playSessionId, bool hasJob, PlayMethod expected)
    {
        var job = new object();
        Assert.Equal(
            expected,
            Api.PlaybackSyncController.ValidatePlayMethod(sent, playSessionId, id => hasJob && id == playSessionId ? job : null));
    }
}
