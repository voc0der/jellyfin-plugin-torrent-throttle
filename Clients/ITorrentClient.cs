namespace Jellyfin.Plugin.TorrentThrottle.Clients;

// Values are bytes per second, matching the Web API. Zero means unlimited only
// when restoring a previously saved value; calculated caps are always positive.
public sealed record SpeedLimits(long Upload, long Download, long AlternativeUpload, long AlternativeDownload);
public sealed record DesiredLimits(long Upload, long? Download);

public interface ITorrentClient : IDisposable
{
    Task<SpeedLimits> GetLimitsAsync(CancellationToken cancellationToken);
    Task SetLimitsAsync(long upload, long alternativeUpload, long? download, long? alternativeDownload, CancellationToken cancellationToken);
}

public interface ITorrentClientFactory
{
    ITorrentClient Create(ConnectionOptions options);
}

public sealed class TorrentClientFactory : ITorrentClientFactory
{
    public ITorrentClient Create(ConnectionOptions options) => new QBittorrentClient(options);
}
