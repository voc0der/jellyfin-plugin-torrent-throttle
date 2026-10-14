using System.Reflection;
using Jellyfin.Plugin.TorrentThrottle.Configuration;
using Jellyfin.Plugin.TorrentThrottle.Throttling;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TorrentThrottle.Tests;

[Collection("Plugin instance")]
public class PlaybackReviewRegressionTests
{
    [Fact]
    public async Task InvalidDisabledConfigurationDoesNotEvaluateOrFaultTheWorker()
    {
        await using var h = new Harness();
        h.Config.Enabled = false;
        h.Config.UnknownStreamMbps = int.MaxValue;
        h.Config.HeadroomPercent = 100000000;
        var session = h.Session();
        session.NowPlayingItem.MediaStreams = [];
        await h.StartAsync("Disabled");
        h.RaiseStart(session);
        await h.PulseAsync();
        Assert.Equal("Disabled", h.Status.Current.State);
        Assert.False(h.Monitor.ExecuteTask!.IsCompleted);
        Assert.Equal(0, h.Client.Reads);
        Assert.Empty(h.Client.Writes);
    }

    [Fact]
    public async Task InvalidEnabledConfigurationIsRejectedBeforeArithmeticAndCanRecover()
    {
        await using var h = new Harness();
        await h.StartAsync();
        var session = h.Session();
        session.NowPlayingItem.MediaStreams = [];
        // Models a hand-edited configuration file rather than the validated save API.
        h.Config.UnknownStreamMbps = int.MaxValue;
        h.Config.HeadroomPercent = 100000000;
        h.RaiseStart(session);
        await Until(() => h.Status.Current.State == "Retrying");
        Assert.False(h.Monitor.ExecuteTask!.IsCompleted);
        h.Config.UnknownStreamMbps = 20;
        h.Config.HeadroomPercent = 0;
        await h.PulseAsync();
        Assert.Equal(2500000, h.Status.Current.ReservedBytesPerSecond);
    }

    [Fact]
    public async Task MetadataFailureCannotFaultTheWorkerAndConfigurationChangeRetriesIt()
    {
        await using var h = new Harness();
        await h.StartAsync();
        var session = h.Session();
        h.RaiseStart(session);
        await Until(() => h.Status.Current.RemoteStreams == 1);
        var streams = session.NowPlayingItem.MediaStreams;
        session.NowPlayingItem.MediaStreams = [null!]; // Force failure in metadata reading, before any HTTP.
        await h.PulseAsync();
        Assert.Equal("Retrying", h.Status.Current.State);
        Assert.False(h.Monitor.ExecuteTask!.IsCompleted);
        var count = h.Client.Writes.Count;
        for (var i = 0; i < 5; i++) { h.RaiseProgress(session); }
        Assert.Equal(count, h.Client.Writes.Count);
        session.NowPlayingItem.MediaStreams = streams;
        await h.PulseAsync();
        Assert.Equal("Throttling", h.Status.Current.State);
    }

    [Fact]
    public async Task SynchronousProgressFailureDoesNotEscapeIntoJellyfin()
    {
        await using var h = new Harness();
        await h.StartAsync();
        var session = h.Session();
        h.Network.Failure = new InvalidOperationException("network classification unavailable");
        Assert.Null(Record.Exception(() => h.RaiseProgress(session)));
        h.Network.Failure = null;
        h.RaiseProgress(session);
        await Until(() => h.Status.Current.RemoteStreams == 1);
        Assert.False(h.Monitor.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task IdleStopWithoutPlaybackIdReleasesOnlyTheMatchingItem()
    {
        await using var h = new Harness();
        await h.StartAsync();
        var session = h.Session();
        h.RaiseStart(session);
        await Until(() => h.Status.Current.RemoteStreams == 1);
        var stop = new PlaybackStopEventArgs { Session = session, MediaInfo = session.NowPlayingItem };
        session.NowPlayingItem = null!;
        session.PlayState = new PlayerStateInfo();
        h.Sessions.Raise(nameof(ISessionManager.PlaybackStopped), stop);
        await Until(() => h.Status.Current.RemoteStreams == 0);
        Assert.Equal(10000 * 1024, h.Status.Current.UploadBytesPerSecond);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectProgressCanResumeTheSamePlaybackRegardlessOfEndEventOrder(bool newProgressFirst)
    {
        await using var h = new Harness();
        await h.StartAsync();
        var old = h.Session();
        h.RaiseStart(old);
        await Until(() => h.Status.Current.RemoteStreams == 1);
        var next = h.Session(item: old.NowPlayingItem);
        var ended = new SessionEventArgs { SessionInfo = old };
        if (newProgressFirst)
        {
            h.RaiseProgress(next);
            h.Sessions.Raise(nameof(ISessionManager.SessionEnded), ended);
        }
        else
        {
            h.Sessions.Raise(nameof(ISessionManager.SessionEnded), ended);
            h.RaiseProgress(next);
        }

        // A delayed event from the ended SessionInfo must not steal the slot back.
        h.RaiseProgress(old);
        await h.PulseAsync();
        Assert.Equal(1000000, h.Status.Current.ReservedBytesPerSecond);
        Assert.Equal(1, h.Status.Current.RemoteStreams);
        h.Sessions.Raise(nameof(ISessionManager.PlaybackStopped), new PlaybackStopEventArgs
        {
            Session = next,
            PlaySessionId = "play",
            MediaInfo = next.NowPlayingItem
        });
        await Until(() => h.Status.Current.RemoteStreams == 0);
        h.RaiseProgress(next);
        await h.PulseAsync();
        Assert.Equal(0, h.Status.Current.RemoteStreams);
    }

    [Fact]
    public async Task RemuxMetadataClearingDoesNotCauseRepeatedLimitWrites()
    {
        await using var h = new Harness();
        await h.StartAsync();
        var session = h.Session();
        session.PlayState.PlayMethod = PlayMethod.DirectStream;
        session.TranscodingInfo = new() { Bitrate = 5000000 };
        h.RaiseStart(session);
        await Until(() => h.Status.Current.RemoteStreams == 1);
        var writes = h.Client.Writes.Count;
        for (var i = 0; i < 20; i++)
        {
            session.TranscodingInfo = i % 2 == 0 ? null! : new() { Bitrate = 5000000 };
            h.RaiseProgress(session);
            await h.PulseAsync();
            Assert.Equal(1000000, h.Status.Current.ReservedBytesPerSecond);
        }

        Assert.Equal(writes, h.Client.Writes.Count);
    }

    [Fact]
    public async Task LateStartAndStopForOldItemCannotRetireTheCurrentPlayback()
    {
        await using var h = new Harness();
        await h.StartAsync();
        var session = h.Session();
        var oldItem = session.NowPlayingItem;
        var oldStart = new PlaybackProgressEventArgs { Session = session, MediaInfo = oldItem, PlaySessionId = "old" };
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, BitRate = 16000000 }]
        };
        h.RaiseStart(session);
        await Until(() => h.Status.Current.ReservedBytesPerSecond == 2000000);
        h.Sessions.Raise(nameof(ISessionManager.PlaybackStart), oldStart);
        h.Sessions.Raise(nameof(ISessionManager.PlaybackStopped), new PlaybackStopEventArgs
        {
            Session = session,
            MediaInfo = oldItem,
            PlaySessionId = "old"
        });
        h.Sessions.Raise(nameof(ISessionManager.PlaybackStopped), new PlaybackStopEventArgs
        {
            Session = session,
            MediaInfo = oldItem // An old item-only idle stop must also be harmless.
        });
        h.RaiseProgress(session);
        await h.PulseAsync();
        Assert.Equal(2000000, h.Status.Current.ReservedBytesPerSecond);
        Assert.Equal(1, h.Status.Current.RemoteStreams);
    }

    [Fact]
    public void ConcurrentSessionFieldClearingCannotThrowFromBitrateRead()
    {
        var session = new SessionInfo(null!, NullLogger.Instance);
        var item = new BaseItemDto { Id = Guid.NewGuid(), MediaStreams = [new MediaStream { Type = MediaStreamType.Video, BitRate = 8000000 }] };
        var state = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode };
        var info = new TranscodingInfo { Bitrate = 2000000 };
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            started.Set();
            while (!stop.IsCancellationRequested)
            {
                session.NowPlayingItem = item;
                session.PlayState = state;
                session.TranscodingInfo = info;
                session.NowPlayingItem = null!;
                session.PlayState = null!;
                session.TranscodingInfo = null!;
            }
        });
        writer.Start();
        try
        {
            started.Wait(TestContext.Current.CancellationToken);
            for (var i = 0; i < 50000; i++) { _ = PlaybackMonitor.GetBitrate(session); }
        }
        finally
        {
            stop.Cancel();
            writer.Join();
        }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { await Task.Delay(10, timeout.Token); }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "torrent-review-" + Guid.NewGuid().ToString("N"));
        private readonly Plugin? _previous = Plugin.Instance;
        private readonly LimitController _limits;
        private readonly ISessionManager _manager;
        public Plugin Plugin { get; }
        public PluginConfiguration Config => Plugin.Configuration;
        public PlaybackMonitorTests.SessionProxy Sessions { get; }
        public PlaybackMonitorTests.NetworkProxy Network { get; }
        public LimitControllerTests.FakeClient Client { get; } = new();
        public ThrottleStatus Status { get; } = new();
        public PlaybackMonitor Monitor { get; }

        public Harness()
        {
            Plugin = new Plugin(new TestApplicationPaths(_directory), new Serializer());
            Config.Enabled = true;
            Config.DelaySeconds = 0;
            Config.HeadroomPercent = 0;
            Config.MaxUploadKiBps = 10000;
            _manager = DispatchProxy.Create<ISessionManager, PlaybackMonitorTests.SessionProxy>();
            Sessions = (PlaybackMonitorTests.SessionProxy)_manager;
            var network = DispatchProxy.Create<INetworkManager, PlaybackMonitorTests.NetworkProxy>();
            Network = (PlaybackMonitorTests.NetworkProxy)network;
            _limits = new(new LimitControllerTests.FakeFactory(Client), new LimitRecoveryStore(Path.Combine(_directory, "recovery.json")));
            Monitor = new PlaybackMonitor(_manager, network, _limits, Status, NullLogger<PlaybackMonitor>.Instance);
        }

        public async Task StartAsync(string state = "Ready")
        {
            await Monitor.StartAsync(CancellationToken.None);
            await Until(() => Status.Current.State == state);
        }

        public SessionInfo Session(string id = "session", BaseItemDto? item = null)
        {
            var session = new SessionInfo(_manager, NullLogger.Instance)
            {
                Id = id,
                RemoteEndPoint = "203.0.113.10",
                PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.DirectPlay },
                NowPlayingItem = item ?? new BaseItemDto
                {
                    Id = Guid.NewGuid(),
                    MediaStreams = [new MediaStream { Type = MediaStreamType.Video, BitRate = 8000000 }]
                }
            };
            Sessions.Sessions.RemoveAll(old => old.Id == id);
            Sessions.Sessions.Add(session);
            return session;
        }

        public void RaiseStart(SessionInfo session) => Sessions.Raise(nameof(ISessionManager.PlaybackStart), Event(session));
        public void RaiseProgress(SessionInfo session) => Sessions.Raise(nameof(ISessionManager.PlaybackProgress), Event(session));
        private static PlaybackProgressEventArgs Event(SessionInfo session) => new()
        {
            Session = session,
            PlaySessionId = "play",
            MediaInfo = session.NowPlayingItem
        };

        public async Task PulseAsync()
        {
            var before = Status.Current;
            Plugin.UpdateConfiguration(Config);
            await Until(() => !ReferenceEquals(Status.Current, before));
        }

        public async ValueTask DisposeAsync()
        {
            await Monitor.StopAsync(CancellationToken.None);
            Monitor.Dispose();
            _limits.Dispose();
            typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, _previous);
            if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
        }

        private sealed class Serializer : IXmlSerializer
        {
            public void SerializeToFile(object obj, string file) { }
            public object DeserializeFromFile(Type type, string file) => throw new NotSupportedException();
            public object DeserializeFromStream(Type type, Stream stream) => throw new NotSupportedException();
            public object DeserializeFromBytes(Type type, byte[] buffer) => throw new NotSupportedException();
            public void SerializeToStream(object obj, Stream stream) => throw new NotSupportedException();
        }
    }
}
