using Jellyfin.Plugin.TorrentThrottle.Configuration;

namespace Jellyfin.Plugin.TorrentThrottle.Throttling;

internal sealed record PlaybackSample(string SessionId, string PlaybackId, Guid ItemId, bool Remote, bool Paused, bool Transcoding, long? Bitrate);
internal sealed record BandwidthBudget(long BytesPerSecond, int ActiveStreams, int PendingStreams, DateTimeOffset? NextEvaluation);

// All state changes happen under PlaybackMonitor's gate. Time is explicit so
// cancellation, overlap and settling can be tested without wall-clock sleeps.
internal sealed class PlaybackTracker
{
    // A dropped WebSocket ends Jellyfin's SessionInfo but not the stream; the
    // client's next progress report arrives on a new one. Hold the reservation
    // meanwhile, and release it if no report arrives within this window.
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(30);

    private sealed class Entry(PlaybackSample sample, DateTimeOffset now)
    {
        public PlaybackSample Sample { get; set; } = sample;
        public DateTimeOffset StartedAt { get; set; } = now;
        public DateTimeOffset? ReadyAt { get; set; } = sample.Transcoding && sample.Bitrate is not > 0 ? null : now;
        public long? SettledBitrate { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<(string Session, string Playback)> _stopped = new();
    private readonly Queue<(string Session, string Playback)> _stoppedOrder = new();
    private readonly HashSet<(string Session, string Playback)> _superseded = new();
    private readonly Queue<(string Session, string Playback)> _supersededOrder = new();
    private readonly Dictionary<string, (Entry Entry, DateTimeOffset At)> _disconnected = new(StringComparer.Ordinal);

    public void Update(PlaybackSample sample, DateTimeOffset now, bool isStart = false)
    {
        if (_stopped.Contains((sample.SessionId, sample.PlaybackId)))
        {
            if (!isStart || !sample.PlaybackId.StartsWith("item:", StringComparison.Ordinal))
            {
                return;
            }

            // Clients without playback IDs can replay the same item. A fresh
            // start is the only event allowed to revive that fallback identity.
            _stopped.Remove((sample.SessionId, sample.PlaybackId));
        }

        if (!_entries.TryGetValue(sample.SessionId, out var entry))
        {
            // Replacement by a queued start is not proof that a playback
            // stopped. Its progress may recover an empty slot after a late
            // start/stop pair; only an actual stop permanently rejects it.
            _superseded.Remove((sample.SessionId, sample.PlaybackId));
            entry = Resume(sample, now);
            if (entry == null)
            {
                _entries[sample.SessionId] = new Entry(sample, now);
                return;
            }
        }

        if (_superseded.Contains((sample.SessionId, sample.PlaybackId)))
        {
            if (!isStart || !sample.PlaybackId.StartsWith("item:", StringComparison.Ordinal))
            {
                return;
            }

            _superseded.Remove((sample.SessionId, sample.PlaybackId));
        }

        if (entry.Sample.PlaybackId != sample.PlaybackId)
        {
            if (entry.Sample.PlaybackId.StartsWith("item:", StringComparison.Ordinal) && entry.Sample.ItemId == sample.ItemId)
            {
                // Startup reconciliation has no PlaySessionId. Adopt the first
                // real report without restarting its settling window.
                entry.Sample = entry.Sample with { PlaybackId = sample.PlaybackId };
            }
            else
            {
                // A late progress report from the previous playback cannot replace
                // the new playback. Only a start event establishes a new identity.
                if (isStart)
                {
                    Remember(_superseded, _supersededOrder, (sample.SessionId, entry.Sample.PlaybackId));
                    _entries[sample.SessionId] = new Entry(sample, now);
                }

                return;
            }
        }

        if (sample.Paused)
        {
            entry.SettledBitrate = null;
        }

        if ((entry.Sample.Paused && !sample.Paused) || (!entry.Sample.Transcoding && sample.Transcoding))
        {
            entry.StartedAt = now;
            entry.ReadyAt = sample.Transcoding && sample.Bitrate is not > 0 ? null : now;
        }
        else if (entry.ReadyAt == null && (!sample.Transcoding || sample.Bitrate is > 0))
        {
            entry.ReadyAt = now;
        }

        entry.Sample = sample;
    }

    public void RefreshMetadata(string sessionId, Guid itemId, bool transcoding, long? bitrate, DateTimeOffset now)
    {
        if (_entries.TryGetValue(sessionId, out var entry) && entry.Sample.ItemId == itemId)
        {
            Update(entry.Sample with { Transcoding = transcoding, Bitrate = bitrate }, now);
        }
    }

    public void Stop(string sessionId, string? playbackId = null)
    {
        if (playbackId != null)
        {
            // A queued stop can precede its queued start callback.
            Retire(sessionId, playbackId);
        }

        if (_entries.TryGetValue(sessionId, out var entry) && (playbackId == null || entry.Sample.PlaybackId == playbackId))
        {
            _entries.Remove(sessionId);
            Retire(sessionId, entry.Sample.PlaybackId);
        }

        if (_disconnected.TryGetValue(sessionId, out var parked) && (playbackId == null || parked.Entry.Sample.PlaybackId == playbackId))
        {
            _disconnected.Remove(sessionId);
            Retire(sessionId, parked.Entry.Sample.PlaybackId);
        }
    }

    public void StopItem(string sessionId, Guid itemId)
    {
        var entry = _entries.GetValueOrDefault(sessionId) ?? (_disconnected.TryGetValue(sessionId, out var parked) ? parked.Entry : null);
        if (entry != null && entry.Sample.ItemId == itemId)
        {
            Stop(sessionId, entry.Sample.PlaybackId);
        }
    }

    // A connection ending is not a playback stop. The same playback ID may
    // return on the next connection's progress report.
    public void Disconnect(string sessionId, DateTimeOffset now)
    {
        if (_entries.Remove(sessionId, out var entry))
        {
            _disconnected[sessionId] = (entry, now);
        }
    }

    // Resumes only the playback that was disconnected. Anything else reported
    // by the session means that playback is over, so it is dropped.
    private Entry? Resume(PlaybackSample sample, DateTimeOffset now)
    {
        if (!_disconnected.Remove(sample.SessionId, out var parked)
            || parked.Entry.Sample.PlaybackId != sample.PlaybackId || now >= parked.At + ReconnectGrace)
        {
            return null;
        }

        _entries[sample.SessionId] = parked.Entry;
        return parked.Entry;
    }

    private void Retire(string sessionId, string playbackId)
        => Remember(_stopped, _stoppedOrder, (sessionId, playbackId));

    private static void Remember(HashSet<(string Session, string Playback)> identities,
        Queue<(string Session, string Playback)> order, (string Session, string Playback) key)
    {
        if (identities.Add(key))
        {
            order.Enqueue(key);
        }

        while (order.Count > 256)
        {
            identities.Remove(order.Dequeue());
        }
    }

    public BandwidthBudget Evaluate(PluginConfiguration config, DateTimeOffset now)
    {
        long bitrate = 0;
        var active = 0;
        var pending = 0;
        DateTimeOffset? next = null;
        foreach (var (sessionId, parked) in _disconnected.ToArray())
        {
            var expires = parked.At + ReconnectGrace;
            if (expires <= now)
            {
                _disconnected.Remove(sessionId);
            }
            else if (next == null || expires < next)
            {
                next = expires;
            }
        }

        foreach (var entry in _entries.Values.Concat(_disconnected.Values.Select(parked => parked.Entry)))
        {
            if (!entry.Sample.Remote || entry.Sample.Paused)
            {
                continue;
            }

            active++;
            // Give delayed transcode metadata 30 seconds to arrive, then use
            // the explicit fallback budget instead of ignoring this stream.
            var ready = entry.ReadyAt ?? entry.StartedAt.AddSeconds(30);
            var due = ready.AddSeconds(config.DelaySeconds);
            if (due > now)
            {
                pending++;
                // A quality/method change must not release bandwidth already
                // reserved by this stream while the replacement settles.
                bitrate += entry.SettledBitrate ?? 0;
                // One early sample lets us notice metadata published just after
                // PlaybackStart, even before the first client progress report.
                var initialSample = entry.StartedAt.AddSeconds(Math.Max(1, config.DelaySeconds));
                if (entry.ReadyAt == null && initialSample > now)
                {
                    due = initialSample;
                }

                if (next == null || due < next)
                {
                    next = due;
                }

                continue;
            }

            entry.SettledBitrate = entry.Sample.Bitrate is > 0 ? entry.Sample.Bitrate.Value : config.UnknownStreamMbps * 1000000L;
            bitrate += entry.SettledBitrate.Value;
        }

        var bytes = (long)Math.Ceiling(bitrate / 8m * (100 + config.HeadroomPercent) / 100m);
        return new BandwidthBudget(bytes, active, pending, next);
    }

    public static Clients.DesiredLimits CalculateLimits(PluginConfiguration config, long reservedBytesPerSecond)
    {
        // qBittorrent stores caps at KiB/s precision. Zero is unlimited, so
        // exhaustion must clamp to 1 KiB/s, never round down to zero.
        var reserveKiB = (long)Math.Ceiling(reservedBytesPerSecond / 1024m);
        return new Clients.DesiredLimits(
            Math.Max(1, config.MaxUploadKiBps - reserveKiB) * 1024,
            config.ThrottleDownload ? Math.Max(1, config.MaxDownloadKiBps - reserveKiB) * 1024 : null);
    }
}
