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

[CollectionDefinition("Plugin instance", DisableParallelization = true)]
public sealed class PluginCollection;

[Collection("Plugin instance")]
public class PlaybackMonitorTests
{
    [Fact]
    public async Task EventPipelineAppliesCapsReleasesOnPauseAndStopAndRestoresOnShutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "torrent-monitor-" + Guid.NewGuid().ToString("N"));
        var previous = Plugin.Instance;
        var plugin = new Plugin(new TestApplicationPaths(directory), new TestSerializer());
        var config = plugin.Configuration;
        config.Enabled = true;
        config.DelaySeconds = 0;
        config.HeadroomPercent = 0;
        config.MaxUploadKiBps = 10000;
        var manager = DispatchProxy.Create<ISessionManager, SessionProxy>();
        var proxy = (SessionProxy)manager;
        var network = DispatchProxy.Create<INetworkManager, NetworkProxy>();
        var client = new LimitControllerTests.FakeClient();
        using var controller = new LimitController(new LimitControllerTests.FakeFactory(client), new LimitRecoveryStore(Path.Combine(directory, "recovery.json")));
        var status = new ThrottleStatus();
        using var monitor = new PlaybackMonitor(manager, network, controller, status, NullLogger<PlaybackMonitor>.Instance);
        var remote = Session(manager, "remote", "203.0.113.10");
        var local = Session(manager, "local", "192.168.1.5");
        try
        {
            await monitor.StartAsync(CancellationToken.None);
            await Until(() => status.Current.State == "Ready");
            Assert.Equal(10000 * 1024, client.Current.Upload);
            proxy.Raise(nameof(ISessionManager.PlaybackStart), Event(remote));
            await Until(() => status.Current.RemoteStreams == 1);
            Assert.Equal(1000000, status.Current.ReservedBytesPerSecond);
            proxy.Raise(nameof(ISessionManager.PlaybackStart), Event(local));
            proxy.Raise(nameof(ISessionManager.PlaybackProgress), Event(remote, true));
            await Until(() => status.Current.RemoteStreams == 0);
            Assert.Equal(10000 * 1024, client.Current.Upload);
            proxy.Raise(nameof(ISessionManager.PlaybackProgress), Event(remote));
            await Until(() => status.Current.RemoteStreams == 1);
            proxy.Raise(nameof(ISessionManager.PlaybackStopped), new PlaybackStopEventArgs { Session = remote, PlaySessionId = "play-remote", MediaInfo = remote.NowPlayingItem });
            await Until(() => status.Current.RemoteStreams == 0);
            Assert.Equal(10000 * 1024, client.Current.Upload);
            await monitor.StopAsync(CancellationToken.None);
            Assert.Equal(0, proxy.SubscriberCount);
            Assert.Equal(new Clients.SpeedLimits(0, 2000, 3000, 4000), client.Current);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, previous);
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    }

    [Fact]
    public void DirectPlayUsesAllAudioTracksButRemuxUsesSelectedAudio()
    {
        var session = Session(null!, "one", "203.0.113.10");
        session.NowPlayingItem.MediaStreams =
        [
            new() { Type = MediaStreamType.Video, BitRate = 6000000, Index = 0 },
            new() { Type = MediaStreamType.Audio, BitRate = 1000000, Index = 1 },
            new() { Type = MediaStreamType.Audio, BitRate = 500000, Index = 2 }
        ];
        Assert.Equal(7500000, PlaybackMonitor.GetBitrate(session));
        session.PlayState.PlayMethod = PlayMethod.DirectStream;
        session.PlayState.AudioStreamIndex = 2;
        Assert.Equal(6500000, PlaybackMonitor.GetBitrate(session));
        session.PlayState.PlayMethod = PlayMethod.Transcode;
        session.TranscodingInfo = new() { Bitrate = 2000000 };
        Assert.Equal(2000000, PlaybackMonitor.GetBitrate(session));
        session.TranscodingInfo = null;
        Assert.Null(PlaybackMonitor.GetBitrate(session));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("stale-source")]
    public void SingleMediaSourceSuppliesBitrateWhenTheSelectedSourceIsMissing(string? mediaSourceId)
    {
        var session = Session(null!, "one", "203.0.113.10");
        session.PlayState.MediaSourceId = mediaSourceId;
        session.NowPlayingItem.MediaSources = [new MediaSourceInfo { Id = "source", Bitrate = 12000000 }];
        session.NowPlayingItem.MediaStreams = [];

        Assert.Equal(12000000, PlaybackMonitor.GetBitrate(session));
    }

    private static PlaybackProgressEventArgs Event(SessionInfo session, bool paused = false) => new()
    {
        Session = session,
        PlaySessionId = "play-" + session.Id,
        MediaInfo = session.NowPlayingItem,
        IsPaused = paused
    };

    private static SessionInfo Session(ISessionManager manager, string id, string address) => new(manager, NullLogger.Instance)
    {
        Id = id,
        RemoteEndPoint = address,
        PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.DirectPlay },
        NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, BitRate = 8000000 }]
        }
    };

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { await Task.Delay(10, timeout.Token); }
    }

    public class SessionProxy : DispatchProxy
    {
        private readonly Dictionary<string, Delegate?> _handlers = new();
        public List<SessionInfo> Sessions { get; } = new();
        public int SubscriberCount => _handlers.Values.Sum(value => value?.GetInvocationList().Length ?? 0);
        public void Raise(string name, EventArgs args) => _handlers.GetValueOrDefault(name)?.DynamicInvoke(this, args);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var name = method!.Name;
            if (name == "get_Sessions") { return Sessions.ToArray(); }
            if (name.StartsWith("add_", StringComparison.Ordinal)) { _handlers[name[4..]] = Delegate.Combine(_handlers.GetValueOrDefault(name[4..]), (Delegate)args![0]!); return null; }
            if (name.StartsWith("remove_", StringComparison.Ordinal)) { _handlers[name[7..]] = Delegate.Remove(_handlers.GetValueOrDefault(name[7..]), (Delegate)args![0]!); return null; }
            throw new NotSupportedException(name);
        }
    }

    public class NetworkProxy : DispatchProxy
    {
        public Exception? Failure { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Failure != null) { throw Failure; }
            return method!.Name == "IsInLocalNetwork"
                ? args![0]!.ToString()!.StartsWith("192.168.", StringComparison.Ordinal) : throw new NotSupportedException();
        }
    }

    private sealed class TestSerializer : IXmlSerializer
    {
        public void SerializeToFile(object obj, string file) { }
        public object DeserializeFromFile(Type type, string file) => throw new NotSupportedException();
        public object DeserializeFromStream(Type type, Stream stream) => throw new NotSupportedException();
        public object DeserializeFromBytes(Type type, byte[] buffer) => throw new NotSupportedException();
        public void SerializeToStream(object obj, Stream stream) => throw new NotSupportedException();
    }
}
