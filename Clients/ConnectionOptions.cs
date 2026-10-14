namespace Jellyfin.Plugin.TorrentThrottle.Clients;

public sealed record ConnectionOptions
{
    public ConnectionOptions(string url, string username, string password, string apiKey)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Enter an HTTP or HTTPS Web UI URL without embedded credentials, a query, or a fragment.");
        }

        BaseUri = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        Username = username ?? string.Empty;
        Password = password ?? string.Empty;
        ApiKey = apiKey?.Trim() ?? string.Empty;
        if (ApiKey.Contains('\r') || ApiKey.Contains('\n'))
        {
            throw new ArgumentException("The API key must be on one line.");
        }
    }

    public Uri BaseUri { get; }
    public string Username { get; }
    public string Password { get; }
    public string ApiKey { get; }
    public override string ToString() => "qBittorrent connection";
}
