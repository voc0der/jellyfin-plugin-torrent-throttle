using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TorrentThrottle.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string WebUiUrl { get; set; } = "http://localhost:8080";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int MaxUploadKiBps { get; set; } = 1024;
    public bool ThrottleDownload { get; set; }
    public int MaxDownloadKiBps { get; set; } = 10240;
    public int DelaySeconds { get; set; } = 5;
    public int HeadroomPercent { get; set; } = 10;
    public int UnknownStreamMbps { get; set; } = 20;

    public void Validate()
    {
        if (MaxUploadKiBps is < 1 or > 100000000 || MaxDownloadKiBps is < 1 or > 100000000)
        {
            throw new ArgumentException("Speed caps must be between 1 and 100000000 KiB/s.");
        }

        if (DelaySeconds is < 0 or > 120 || HeadroomPercent is < 0 or > 100 || UnknownStreamMbps is < 1 or > 100000)
        {
            throw new ArgumentException("Delay must be 0–120 seconds, headroom 0–100%, and fallback bitrate 1–100000 Mbps.");
        }

        _ = GetConnection();
    }

    public Clients.ConnectionOptions GetConnection() => new(WebUiUrl, Username, Password, ApiKey);
}
