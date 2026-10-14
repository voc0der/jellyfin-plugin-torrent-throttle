using Jellyfin.Plugin.TorrentThrottle.Configuration;
using Jellyfin.Plugin.TorrentThrottle.Throttling;

namespace Jellyfin.Plugin.TorrentThrottle.Tests;

public class PlaybackTrackerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-13T12:00:00Z");
    private static readonly Guid Item = Guid.NewGuid();
    private readonly PluginConfiguration _config = new() { HeadroomPercent = 0 };
    private readonly PlaybackTracker _tracker = new();
    private static PlaybackSample Sample(string session = "one", long? bitrate = 8000000) => new(session, "play-" + session, Item, true, false, false, bitrate);

    [Fact]
    public void WaitsFiveSecondsAndDuplicateStartsDoNotExtendDelay()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Update(Sample(), Start.AddSeconds(3), true);
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(4)).BytesPerSecond);
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(5)).BytesPerSecond);
    }

    [Fact]
    public void OverlappingStreamsSettleIndependentlyAndStoppingReleasesOnlyOne()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Update(Sample("two", 16000000), Start.AddSeconds(3), true);
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(5)).BytesPerSecond);
        Assert.Equal(3000000, _tracker.Evaluate(_config, Start.AddSeconds(8)).BytesPerSecond);
        _tracker.Stop("one", "play-one");
        Assert.Equal(2000000, _tracker.Evaluate(_config, Start.AddSeconds(9)).BytesPerSecond);
    }

    [Fact]
    public void StopBeforeSettlingAndLateProgressCannotResurrectPlayback()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Stop("one", "play-one");
        _tracker.Update(Sample(), Start.AddSeconds(6));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(10)).ActiveStreams);
    }

    [Fact]
    public void OldStopAndProgressDoNotReplaceNewPlayback()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Update(Sample() with { PlaybackId = "next", Bitrate = 16000000 }, Start.AddSeconds(2), true);
        _tracker.Stop("one", "play-one");
        _tracker.Update(Sample(), Start.AddSeconds(4));
        Assert.Equal(2000000, _tracker.Evaluate(_config, Start.AddSeconds(7)).BytesPerSecond);
    }

    [Fact]
    public void PauseReleasesImmediatelyAndResumeSettlesAgain()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Update(Sample() with { Paused = true }, Start.AddSeconds(6));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(6)).BytesPerSecond);
        _tracker.Update(Sample(), Start.AddSeconds(10));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(14)).BytesPerSecond);
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(15)).BytesPerSecond);
    }

    [Fact]
    public void TranscodeDelayBeginsAfterOutputBitrateArrives()
    {
        _tracker.Update(Sample(bitrate: null) with { Transcoding = true }, Start, true);
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(5)).BytesPerSecond);
        _tracker.RefreshMetadata("one", Item, true, 4000000, Start.AddSeconds(7));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(11)).BytesPerSecond);
        Assert.Equal(500000, _tracker.Evaluate(_config, Start.AddSeconds(12)).BytesPerSecond);
    }

    [Fact]
    public void MissingTranscodeMetadataUsesFallbackAfterBoundedWait()
    {
        _tracker.Update(Sample(bitrate: null) with { Transcoding = true }, Start, true);
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(34)).BytesPerSecond);
        Assert.Equal(2500000, _tracker.Evaluate(_config, Start.AddSeconds(35)).BytesPerSecond);
    }

    [Fact]
    public void UnknownDirectPlayUsesFallbackAndLocalPlaybackIsExcluded()
    {
        _tracker.Update(Sample(bitrate: null), Start, true);
        _tracker.Update(Sample("lan", 100000000) with { Remote = false }, Start, true);
        var budget = _tracker.Evaluate(_config, Start.AddSeconds(5));
        Assert.Equal(2500000, budget.BytesPerSecond);
        Assert.Equal(1, budget.ActiveStreams);
    }

    [Fact]
    public void HeadroomIsAppliedAndRateUpdatesDoNotRestartSettling()
    {
        _config.HeadroomPercent = 10;
        _tracker.Update(Sample(), Start, true);
        _tracker.Update(Sample(bitrate: 16000000), Start.AddSeconds(6));
        Assert.Equal(2200000, _tracker.Evaluate(_config, Start.AddSeconds(6)).BytesPerSecond);
    }

    [Fact]
    public void StartupIdentityAdoptsClientPlaybackIdWithoutDelayingAgain()
    {
        _tracker.Update(Sample() with { PlaybackId = "item:" + Item.ToString("N") }, Start, true);
        _tracker.Update(Sample(), Start.AddSeconds(4));
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(5)).BytesPerSecond);
        _tracker.Stop("one", "play-one");
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(6)).ActiveStreams);
    }

    [Fact]
    public void StopDeliveredBeforeStartDoesNotCreateAReservation()
    {
        _tracker.Stop("one", "play-one");
        _tracker.Update(Sample(), Start, true);
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(6)).ActiveStreams);
    }

    [Fact]
    public void LateStartOfReplacedPlaybackIsIgnored()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Update(Sample() with { PlaybackId = "next", Bitrate = 16000000 }, Start.AddSeconds(1), true);
        _tracker.Update(Sample(), Start.AddSeconds(2), true);
        Assert.Equal(2000000, _tracker.Evaluate(_config, Start.AddSeconds(6)).BytesPerSecond);
    }

    [Fact]
    public void ReorderedStartStartStopCannotPermanentlyRejectTheSurvivingPlayback()
    {
        _config.DelaySeconds = 0;
        var current = Sample() with { PlaybackId = "B", ItemId = Guid.NewGuid() };
        var late = Sample() with { PlaybackId = "A", ItemId = Guid.NewGuid() };
        _tracker.Update(current, Start, true);
        _tracker.Update(late, Start, true);
        _tracker.Stop("one", "A");
        _tracker.Update(current, Start.AddSeconds(1));
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(2)).BytesPerSecond);
    }

    [Fact]
    public void ClientWithoutPlaybackIdsCanReturnToAPreviouslySupersededItem()
    {
        var first = Sample() with { PlaybackId = "item:" + Item.ToString("N") };
        var nextItem = Guid.NewGuid();
        var next = Sample() with { ItemId = nextItem, PlaybackId = "item:" + nextItem.ToString("N") };
        _tracker.Update(first, Start, true);
        _tracker.Update(next, Start.AddSeconds(1), true);
        _tracker.Update(first, Start.AddSeconds(2), true);
        _tracker.Update(first with { Paused = true }, Start.AddSeconds(8));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(8)).ActiveStreams);
    }

    [Fact]
    public void ReplayingAnItemWithoutPlaybackIdStillAcceptsPauseReports()
    {
        var sample = Sample() with { PlaybackId = "item:" + Item.ToString("N") };
        _tracker.Update(sample, Start, true);
        _tracker.Stop("one", sample.PlaybackId);
        _tracker.Update(sample, Start.AddSeconds(6), true);
        _tracker.Update(sample with { Paused = true }, Start.AddSeconds(12));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(12)).ActiveStreams);
    }

    [Fact]
    public void ItemOnlyStopReleasesTheRealPlaybackIdentity()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.StopItem("one", Item);
        _tracker.Update(Sample(), Start.AddSeconds(1));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(6)).ActiveStreams);
    }

    [Fact]
    public void ItemOnlyStopCannotRemoveADifferentItem()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.StopItem("one", Guid.NewGuid());
        Assert.Equal(1, _tracker.Evaluate(_config, Start.AddSeconds(6)).ActiveStreams);
    }

    [Fact]
    public void DisconnectAllowsProgressToResumeButRealStopDoesNot()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Disconnect("one", Start);
        _tracker.Update(Sample(), Start.AddSeconds(1));
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(6)).BytesPerSecond);
        _tracker.Stop("one", "play-one");
        _tracker.Disconnect("one", Start.AddSeconds(6));
        _tracker.Update(Sample(), Start.AddSeconds(7));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(12)).ActiveStreams);
    }

    [Fact]
    public void ReconnectKeepsTheSettledReservationWithoutSettlingAgain()
    {
        _tracker.Update(Sample(), Start, true);
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(5)).BytesPerSecond);
        _tracker.Disconnect("one", Start.AddSeconds(10));
        var gap = _tracker.Evaluate(_config, Start.AddSeconds(15));
        Assert.Equal(1000000, gap.BytesPerSecond);
        Assert.Equal(Start.AddSeconds(40), gap.NextEvaluation);
        _tracker.Update(Sample(), Start.AddSeconds(20));
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(20)).BytesPerSecond);
        var resumed = _tracker.Evaluate(_config, Start.AddSeconds(41));
        Assert.Equal(1000000, resumed.BytesPerSecond);
        Assert.Null(resumed.NextEvaluation);
    }

    [Fact]
    public void DisconnectWithoutReconnectReleasesAfterTheGracePeriod()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Evaluate(_config, Start.AddSeconds(5));
        _tracker.Disconnect("one", Start.AddSeconds(10));
        Assert.Equal(1, _tracker.Evaluate(_config, Start.AddSeconds(39)).ActiveStreams);
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(40)).ActiveStreams);
        // A report after the window is treated as a new stream and settles again.
        _tracker.Update(Sample(), Start.AddSeconds(41));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(45)).BytesPerSecond);
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(46)).BytesPerSecond);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopReleasesADisconnectedReservationImmediately(bool itemOnly)
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Evaluate(_config, Start.AddSeconds(5));
        _tracker.Disconnect("one", Start.AddSeconds(10));
        if (itemOnly)
        {
            _tracker.StopItem("one", Item);
        }
        else
        {
            _tracker.Stop("one", "play-one");
        }

        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(11)).ActiveStreams);
        _tracker.Update(Sample(), Start.AddSeconds(12));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(20)).ActiveStreams);
    }

    [Fact]
    public void NewPlaybackAfterReconnectReplacesTheDisconnectedOne()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Evaluate(_config, Start.AddSeconds(5));
        _tracker.Disconnect("one", Start.AddSeconds(10));
        _tracker.Update(Sample() with { PlaybackId = "next" }, Start.AddSeconds(12), true);
        var budget = _tracker.Evaluate(_config, Start.AddSeconds(13));
        Assert.Equal(1, budget.ActiveStreams);
        Assert.Equal(0, budget.BytesPerSecond);
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(17)).BytesPerSecond);
    }

    [Fact]
    public void MethodChangePreservesReservationUntilNewBitrateSettles()
    {
        _tracker.Update(Sample(), Start, true);
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(5)).BytesPerSecond);
        _tracker.Update(Sample(bitrate: null) with { Transcoding = true }, Start.AddSeconds(10));
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(11)).BytesPerSecond);
        _tracker.RefreshMetadata("one", Item, true, 4000000, Start.AddSeconds(14));
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(18)).BytesPerSecond);
        Assert.Equal(500000, _tracker.Evaluate(_config, Start.AddSeconds(19)).BytesPerSecond);
    }

    [Fact]
    public void MethodChangeKeepsOldBudgetThroughMetadataTimeoutThenUsesFallback()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Evaluate(_config, Start.AddSeconds(5));
        _tracker.Update(Sample(bitrate: null) with { Transcoding = true }, Start.AddSeconds(10));
        Assert.Equal(1000000, _tracker.Evaluate(_config, Start.AddSeconds(44)).BytesPerSecond);
        Assert.Equal(2500000, _tracker.Evaluate(_config, Start.AddSeconds(45)).BytesPerSecond);
    }

    [Fact]
    public void PauseOrStopDuringMethodChangeReleasesTheHeldReservation()
    {
        _tracker.Update(Sample(), Start, true);
        _tracker.Evaluate(_config, Start.AddSeconds(5));
        var transcoding = Sample(bitrate: null) with { Transcoding = true };
        _tracker.Update(transcoding, Start.AddSeconds(10));
        _tracker.Update(transcoding with { Paused = true }, Start.AddSeconds(11));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(11)).BytesPerSecond);
        _tracker.Update(transcoding, Start.AddSeconds(12));
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(13)).BytesPerSecond);
        _tracker.Stop("one", "play-one");
        Assert.Equal(0, _tracker.Evaluate(_config, Start.AddSeconds(60)).BytesPerSecond);
    }

    [Theory]
    [InlineData(0, 1048576)]
    [InlineData(1, 1047552)]
    [InlineData(8000000, 1024)]
    public void CapsRoundDownAtKiBPrecisionButNeverBecomeUnlimited(long reserve, long upload)
    {
        var caps = PlaybackTracker.CalculateLimits(_config, reserve);
        Assert.Equal(upload, caps.Upload);
        Assert.Null(caps.Download);
    }

    [Fact]
    public void OptionalDownloadUsesSameReservationAndOwnMaximum()
    {
        _config.ThrottleDownload = true;
        _config.MaxDownloadKiBps = 2000;
        var caps = PlaybackTracker.CalculateLimits(_config, 102400);
        Assert.Equal(924 * 1024, caps.Upload);
        Assert.Equal(1900 * 1024, caps.Download);
    }
}
