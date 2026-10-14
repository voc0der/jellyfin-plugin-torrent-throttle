using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Jellyfin.Plugin.TorrentThrottle.Configuration;
using Jellyfin.Plugin.TorrentThrottle.Throttling;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrentThrottle;

public sealed class PlaybackMonitor(
    ISessionManager sessions,
    INetworkManager network,
    LimitController limits,
    ThrottleStatus status,
    ILogger<PlaybackMonitor> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly PlaybackTracker _tracker = new();
    private readonly ConditionalWeakTable<SessionInfo, object> _endedSessions = new();
    private readonly Dictionary<string, SessionInfo> _trackedSessions = new(StringComparer.Ordinal);
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private Plugin? _plugin;
    private bool _configurationChanged;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _plugin = Plugin.Instance;
        sessions.PlaybackStart += OnStart;
        sessions.PlaybackProgress += OnProgress;
        sessions.PlaybackStopped += OnStop;
        sessions.SessionEnded += OnSessionEnded;
        if (_plugin != null)
        {
            _plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        // Reconcile sessions already playing if the service starts late.
        HandleEvent(() =>
        {
            foreach (var session in sessions.Sessions)
            {
                Track(new PlaybackProgressEventArgs { Session = session, IsPaused = session.PlayState?.IsPaused == true }, true);
            }
        });

        return base.StartAsync(cancellationToken);
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs args) => HandleEvent(() => Track(args, true));
    private void OnProgress(object? sender, PlaybackProgressEventArgs args) => HandleEvent(() => Track(args, false));

    private void HandleEvent(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Progress is invoked synchronously by Jellyfin. A plugin failure
            // must never fail the client's playback request.
            logger.LogWarning("Torrent Throttle could not process a playback event ({FailureType})", ex.GetType().Name);
        }
    }

    private void Track(PlaybackProgressEventArgs args, bool isStart)
    {
        var session = args.Session;
        if (session == null || string.IsNullOrEmpty(session.Id))
        {
            return;
        }

        lock (_gate)
        {
            if (_endedSessions.TryGetValue(session, out _))
            {
                return;
            }

            var metadata = ReadMetadata(session);
            var itemId = args.Item?.Id ?? args.MediaInfo?.Id ?? metadata?.ItemId;
            // Start/stop notifications are queued independently by Jellyfin.
            // An old start must not replace what the session is playing now.
            if (metadata == null || itemId != metadata.ItemId
                || (!string.IsNullOrEmpty(args.MediaSourceId) && !string.IsNullOrEmpty(metadata.MediaSourceId)
                    && args.MediaSourceId != metadata.MediaSourceId))
            {
                return;
            }

            // If a reconnect's progress arrives before the old SessionEnded,
            // accept the SessionInfo that Jellyfin currently owns. Late events
            // from the previous instance cannot replace it.
            var now = DateTimeOffset.UtcNow;
            if (_trackedSessions.TryGetValue(session.Id, out var previous) && !ReferenceEquals(previous, session))
            {
                if (!sessions.Sessions.Any(candidate => ReferenceEquals(candidate, session)))
                {
                    return;
                }

                _tracker.Disconnect(session.Id, now);
            }

            var endpoint = session.RemoteEndPoint;
            var remote = string.IsNullOrEmpty(endpoint) || !network.IsInLocalNetwork(endpoint);
            var sample = new PlaybackSample(session.Id, PlaybackIdentity(args, metadata.ItemId), metadata.ItemId,
                remote, args.IsPaused, metadata.Transcoding, metadata.Bitrate);
            _trackedSessions[session.Id] = session;
            _tracker.Update(sample, now, isStart);
        }

        SignalPlayback();
    }

    private static string PlaybackIdentity(PlaybackProgressEventArgs args, Guid itemId) =>
        string.IsNullOrEmpty(args.PlaySessionId) ? "item:" + itemId.ToString("N") : args.PlaySessionId;

    private sealed record PlaybackMetadata(Guid ItemId, string? MediaSourceId, bool Transcoding, long? Bitrate);

    internal static long? GetBitrate(SessionInfo session) => ReadMetadata(session)?.Bitrate;

    private static PlaybackMetadata? ReadMetadata(SessionInfo session)
    {
        // Jellyfin can clear/replace these references on another thread. Keep
        // each reference and the mutable scalar fields from exactly one read.
        var item = session.NowPlayingItem;
        var state = session.PlayState;
        var transcode = session.TranscodingInfo;
        if (item == null)
        {
            return null;
        }

        var method = state?.PlayMethod;
        var mediaSourceId = state?.MediaSourceId;
        var audioIndex = state?.AudioStreamIndex;
        var transcodeBitrate = transcode?.Bitrate;
        if (method == PlayMethod.Transcode)
        {
            return new(item.Id, mediaSourceId, true, transcodeBitrate is > 0 ? transcodeBitrate : null);
        }

        var sources = item.MediaSources;
        var source = sources?.FirstOrDefault(candidate => candidate.Id == mediaSourceId);
        if (source == null && sources?.Length == 1)
        {
            source = sources[0];
        }

        var sourceBitrate = source?.Bitrate;
        var streams = item.MediaStreams;
        // For remux use the selected media streams consistently. Jellyfin
        // clears TranscodingInfo on every non-transcode progress report, so
        // switching between it and metadata would continually rewrite caps.
        var total = streams?.Where(stream => stream.Type == MediaStreamType.Video
            || (stream.Type == MediaStreamType.Audio && (method != PlayMethod.DirectStream
                || audioIndex == null || stream.Index == audioIndex)))
            .Sum(stream => (long)Math.Max(0, stream.BitRate ?? 0));
        var bitrate = method == PlayMethod.DirectStream && total is > 0 ? total
            : sourceBitrate is > 0 ? sourceBitrate : total is > 0 ? total : null;
        return new(item.Id, mediaSourceId, false, bitrate);
    }

    private void OnStop(object? sender, PlaybackStopEventArgs args) => HandleEvent(() =>
    {
        if (args.Session == null)
        {
            return;
        }

        var item = args.Item?.Id ?? args.MediaInfo?.Id;
        lock (_gate)
        {
            if (!_trackedSessions.TryGetValue(args.Session.Id, out var current) || ReferenceEquals(current, args.Session))
            {
                // A stopped event may arrive after the next start. Match the
                // playback identity, not Session.NowPlayingItem (already changed).
                if (!string.IsNullOrEmpty(args.PlaySessionId))
                {
                    _tracker.Stop(args.Session.Id, args.PlaySessionId);
                }
                else if (item.HasValue)
                {
                    _tracker.StopItem(args.Session.Id, item.Value);
                }
            }
        }

        SignalPlayback();
    });

    private void OnSessionEnded(object? sender, SessionEventArgs args) => HandleEvent(() =>
    {
        var session = args.SessionInfo;
        if (session == null)
        {
            return;
        }

        lock (_gate)
        {
            _endedSessions.GetValue(session, _ => new object());
            if (_trackedSessions.TryGetValue(session.Id, out var current) && ReferenceEquals(current, session))
            {
                _tracker.Disconnect(session.Id, DateTimeOffset.UtcNow);
                _trackedSessions.Remove(session.Id);
            }
        }

        SignalPlayback();
    });

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        lock (_gate)
        {
            _configurationChanged = true;
        }

        Signal();
    }

    private void Signal() => _signals.Writer.TryWrite(true);

    private void SignalPlayback()
    {
        // Keep event state current while disabled, without waking the worker
        // for calculations or disk reads. Config changes and retries still wake it.
        if (_plugin?.Configuration.Enabled == true)
        {
            Signal();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? retryAt = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var budget = new BandwidthBudget(0, 0, 0, null);
            try
            {
                var now = DateTimeOffset.UtcNow;
                lock (_gate)
                {
                    if (_configurationChanged)
                    {
                        retryAt = null;
                        _configurationChanged = false;
                    }
                }

                if (retryAt > now)
                {
                    await WaitForSignalAsync(retryAt, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var config = _plugin?.Configuration ?? new PluginConfiguration();
                if (config.Enabled)
                {
                    // Validate before arithmetic, including configs loaded from
                    // XML. Disabled configs never enter the budget calculation.
                    config.Validate();
                    lock (_gate)
                    {
                        foreach (var session in _trackedSessions.Values)
                        {
                            var metadata = ReadMetadata(session);
                            if (metadata != null)
                            {
                                _tracker.RefreshMetadata(session.Id, metadata.ItemId, metadata.Transcoding, metadata.Bitrate, now);
                            }
                        }

                        budget = _tracker.Evaluate(config, now);
                    }

                    var desired = PlaybackTracker.CalculateLimits(config, budget.BytesPerSecond);
                    await limits.ApplyAsync(config.GetConnection(), desired, stoppingToken).ConfigureAwait(false);
                    status.Set(new ThrottleSnapshot(budget.PendingStreams > 0 ? "Settling" : budget.ActiveStreams > 0 ? "Throttling" : "Ready",
                        budget.ActiveStreams, budget.PendingStreams, budget.BytesPerSecond, desired.Upload, desired.Download));
                }
                else
                {
                    await limits.RestoreAsync(TryGetConnection(config), stoppingToken).ConfigureAwait(false);
                    status.Set(new ThrottleSnapshot("Disabled"));
                }

                retryAt = null;
                await WaitForSignalAsync(budget.NextEvaluation, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Includes metadata reads, calculation and waiting, not just
                // HTTP. An ordinary plugin failure must never fault the hosted
                // service, which would stop Jellyfin under its default policy.
                logger.LogWarning("Torrent Throttle could not update or restore limits ({FailureType}); retrying in 30 seconds", ex.GetType().Name);
                status.Set(new ThrottleSnapshot("Retrying", budget.ActiveStreams, budget.PendingStreams, budget.BytesPerSecond,
                    Error: FailureMessage(ex)));
                retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }
    }

    private async Task WaitForSignalAsync(DateTimeOffset? due, CancellationToken stoppingToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (due.HasValue)
        {
            wait.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (due.Value - DateTimeOffset.UtcNow).TotalMilliseconds)));
        }

        try
        {
            await _signals.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Baseline/retry deadline reached.
        }
    }

    internal static string FailureMessage(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        ArgumentException => "Invalid connection or bandwidth settings. Check the saved configuration.",
        HttpRequestException => "The qBittorrent Web API request failed. Check network access, the URL, credentials and Web UI permissions.",
        OperationCanceledException => "The qBittorrent Web API request timed out. Check network access from Jellyfin.",
        _ => "Could not update or restore torrent limits. Check Jellyfin's log and the saved-limit recovery file."
    };

    private static Clients.ConnectionOptions? TryGetConnection(PluginConfiguration config)
    {
        try
        {
            return config.GetConnection();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        sessions.PlaybackStart -= OnStart;
        sessions.PlaybackProgress -= OnProgress;
        sessions.PlaybackStopped -= OnStop;
        sessions.SessionEnded -= OnSessionEnded;
        if (_plugin != null)
        {
            _plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await limits.RestoreAsync(TryGetConnection(_plugin?.Configuration ?? new PluginConfiguration()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Torrent Throttle could not restore limits during shutdown ({FailureType}); saved limits will be recovered on restart", ex.GetType().Name);
        }
    }
}
