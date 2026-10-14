using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jellyfin.Plugin.TorrentThrottle.Clients;

public sealed class QBittorrentClient : ITorrentClient
{
    private static readonly IPNetwork[] PrivateNetworks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10")
    ];

    private readonly HttpClient _http;
    private readonly ConnectionOptions _options;
    private bool _authenticated;

    public QBittorrentClient(ConnectionOptions options)
        : this(options, new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
            // Self-signed certificates are normal on LAN and Docker networks, so a
            // private or loopback IP skips validation. Redirects are off, so
            // requests never leave that address. Host names are always verified.
            ServerCertificateCustomValidationCallback = IsPrivateAddress(options.BaseUri)
                ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                : null
        })
    {
    }

    internal static bool IsPrivateAddress(Uri uri)
    {
        if (!IPAddress.TryParse(uri.DnsSafeHost, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return PrivateNetworks.Any(network => network.Contains(address));
    }

    internal QBittorrentClient(ConnectionOptions options, HttpMessageHandler handler)
    {
        _options = options;
        _http = new HttpClient(handler) { BaseAddress = options.BaseUri, Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Referrer = options.BaseUri;
        _http.DefaultRequestHeaders.Add("Origin", options.BaseUri.GetLeftPart(UriPartial.Authority));
        if (options.ApiKey.Length > 0)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    public async Task<SpeedLimits> GetLimitsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync("app/preferences", null, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = json.RootElement;
        return new SpeedLimits(ReadLimit(root, "up_limit"), ReadLimit(root, "dl_limit"), ReadLimit(root, "alt_up_limit"), ReadLimit(root, "alt_dl_limit"));
    }

    private static long ReadLimit(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || !property.TryGetInt64(out var value) || value < -1)
        {
            throw new InvalidOperationException("qBittorrent returned an invalid speed limit.");
        }

        return Math.Max(0, value);
    }

    public async Task SetLimitsAsync(long upload, long alternativeUpload, long? download, long? alternativeDownload, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, long> { ["up_limit"] = upload, ["alt_up_limit"] = alternativeUpload };
        if (download.HasValue && alternativeDownload.HasValue)
        {
            values["dl_limit"] = download.Value;
            values["alt_dl_limit"] = alternativeDownload.Value;
        }

        var form = new Dictionary<string, string> { ["json"] = JsonSerializer.Serialize(values) };
        using var response = await SendAsync("app/setPreferences", form, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = _options.Username,
            ["password"] = _options.Password
        });
        using var response = await _http.PostAsync("api/v2/auth/login", content, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        if ((await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim() != "Ok.")
        {
            throw new InvalidOperationException("qBittorrent rejected the username or password.");
        }

        _authenticated = true;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, Dictionary<string, string>? form, CancellationToken cancellationToken)
    {
        if (_options.ApiKey.Length == 0 && !_authenticated)
        {
            await LoginAsync(cancellationToken).ConfigureAwait(false);
        }

        // A cookie can expire between events. Re-authenticate once, without ever
        // hardcoding its name (SID and QBT_SID are both used by released builds).
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, "api/v2/" + path);
            if (form != null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }

            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                && _options.ApiKey.Length == 0 && attempt == 0)
            {
                response.Dispose();
                _authenticated = false;
                await LoginAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                EnsureSuccess(response);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Never include response bodies, URLs, cookies, or credentials in errors.
            throw new HttpRequestException("qBittorrent Web API returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ". Check the URL, credentials and Web UI access settings.", null, response.StatusCode);
        }
    }

    public void Dispose() => _http.Dispose();
}
