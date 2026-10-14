namespace Jellyfin.Plugin.TorrentThrottle.Throttling;

public sealed record ThrottleSnapshot(string State, int RemoteStreams = 0, int PendingStreams = 0, long ReservedBytesPerSecond = 0,
    long? UploadBytesPerSecond = null, long? DownloadBytesPerSecond = null, string? Error = null);

public sealed class ThrottleStatus
{
    private ThrottleSnapshot _current = new("Disabled");
    public ThrottleSnapshot Current => Volatile.Read(ref _current);
    internal void Set(ThrottleSnapshot value) => Volatile.Write(ref _current, value);
}
