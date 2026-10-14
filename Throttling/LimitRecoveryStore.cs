using System.Text.Json;
using Jellyfin.Plugin.TorrentThrottle.Clients;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.TorrentThrottle.Throttling;

public sealed record LimitRecovery(string Endpoint, SpeedLimits Original, bool DownloadOwned);

public class LimitRecoveryStore
{
    private readonly string _path;

    public LimitRecoveryStore(IApplicationPaths paths)
        : this(Path.Combine(paths.PluginConfigurationsPath, "TorrentThrottle.limits.json"))
    {
    }

    internal LimitRecoveryStore(string path) => _path = path;

    public LimitRecovery? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<LimitRecovery>(File.ReadAllText(_path))
            ?? throw new InvalidOperationException("The saved torrent limits could not be read. Restore the saved limits before removing the recovery file.");
    }

    public void Save(LimitRecovery recovery)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(recovery));
        File.Move(temporary, _path, true);
    }

    // An unreadable directory also makes File.Exists false; that is not a removal.
    public bool WasRemoved() => Directory.Exists(Path.GetDirectoryName(_path)) && !File.Exists(_path);

    public void Clear() => File.Delete(_path);
}
