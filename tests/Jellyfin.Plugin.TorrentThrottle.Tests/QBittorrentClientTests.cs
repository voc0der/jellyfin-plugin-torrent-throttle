using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.TorrentThrottle.Clients;

namespace Jellyfin.Plugin.TorrentThrottle.Tests;

public class QBittorrentClientTests
{
    private const string Preferences = "{\"up_limit\":0,\"dl_limit\":2048,\"alt_up_limit\":1024,\"alt_dl_limit\":4096}";

    [Fact]
    public async Task ApiKeySkipsLoginAndPreservesProxyPathAndOrigin()
    {
        using var client = new QBittorrentClient(new("https://example.test/qbit", "user", "password", "qbt_test"), new Handler(request =>
        {
            Assert.Equal("/qbit/api/v2/app/preferences", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer qbt_test", request.Headers.Authorization!.ToString());
            Assert.Equal("https://example.test/qbit/", request.Headers.Referrer!.AbsoluteUri);
            Assert.Equal("https://example.test", request.Headers.GetValues("Origin").Single());
            return Ok(Preferences);
        }));
        Assert.Equal(new SpeedLimits(0, 2048, 1024, 4096), await client.GetLimitsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Http200FailedLoginIsAnAuthenticationFailure()
    {
        var calls = 0;
        using var client = new QBittorrentClient(new("http://example.test", "user", "password", ""), new Handler(_ => { calls++; return Ok("Fails."); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLimitsAsync(CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EmptyNoContentLoginIsASuccessOnQBittorrent52()
    {
        using var client = new QBittorrentClient(new("http://example.test", "user", "password", ""), new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("auth/login") ? new(HttpStatusCode.NoContent) : Ok(Preferences)));
        Assert.Equal(new SpeedLimits(0, 2048, 1024, 4096), await client.GetLimitsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredPasswordSessionRetriesExactlyOnce()
    {
        var logins = 0;
        var reads = 0;
        using var client = new QBittorrentClient(new("http://example.test", "user", "password", ""), new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("auth/login")) { logins++; return Ok("Ok."); }
            reads++;
            return new(HttpStatusCode.Forbidden);
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLimitsAsync(CancellationToken.None));
        Assert.Equal(2, logins);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task InvalidApiKeyDoesNotFallBackToPassword()
    {
        var calls = 0;
        using var client = new QBittorrentClient(new("http://example.test", "user", "password", "invalid"), new Handler(_ => { calls++; return new(HttpStatusCode.Forbidden); }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLimitsAsync(CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UpdatesBothUploadProfilesWithFormEncodedJsonAndOmitsDownloads()
    {
        string? body = null;
        using var client = new QBittorrentClient(new("http://example.test", "", "", "key"), new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("app/setPreferences", request.RequestUri!.AbsolutePath);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            body = await request.Content.ReadAsStringAsync();
            return Ok("");
        }));
        await client.SetLimitsAsync(1024, 2048, null, null, CancellationToken.None);
        using var json = JsonDocument.Parse(WebUtility.UrlDecode(body![5..]));
        Assert.Equal(1024, json.RootElement.GetProperty("up_limit").GetInt64());
        Assert.Equal(2048, json.RootElement.GetProperty("alt_up_limit").GetInt64());
        Assert.False(json.RootElement.TryGetProperty("dl_limit", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"up_limit\":\"bad\"}")]
    public async Task MalformedPreferencesCannotBecomeASnapshot(string response)
    {
        using var client = new QBittorrentClient(new("http://example.test", "", "", "key"), new Handler(_ => Ok(response)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLimitsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("file:///tmp/qbit")]
    [InlineData("http://user:password@example.test")]
    [InlineData("https://example.test/?secret=value")]
    [InlineData("https://example.test/#fragment")]
    public void RejectsAmbiguousOrCredentialBearingUrls(string url) => Assert.Throws<ArgumentException>(() => new ConnectionOptions(url, "", "", ""));

    [Fact]
    public void ConnectionDescriptionNeverContainsCredentials()
    {
        var options = new ConnectionOptions("http://localhost", "secret-user", "secret-password", "secret-key");
        Assert.DoesNotContain("secret", options.ToString());
    }

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : this(request => Task.FromResult(send(request))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
