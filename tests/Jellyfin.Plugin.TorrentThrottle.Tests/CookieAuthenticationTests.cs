using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.TorrentThrottle.Clients;

namespace Jellyfin.Plugin.TorrentThrottle.Tests;

public class CookieAuthenticationTests
{
    [Theory]
    [InlineData("SID")]
    [InlineData("QBT_SID")]
    public async Task RealHttpTransportStoresTheServersCookieNameAndEncodesCredentials(string cookieName)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var url = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            var login = await listener.GetContextAsync().WaitAsync(timeout.Token);
            var body = await new StreamReader(login.Request.InputStream).ReadToEndAsync(timeout.Token);
            Assert.Equal("/api/v2/auth/login", login.Request.RawUrl);
            Assert.Contains("password=p%26%3D%2B", body);
            login.Response.Headers.Add("Set-Cookie", cookieName + "=opaque-token; HttpOnly; Path=/");
            await Respond(login, "Ok.");
            var read = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Assert.Equal(cookieName + "=opaque-token", read.Request.Headers["Cookie"]);
            await Respond(read, "{\"up_limit\":0,\"dl_limit\":0,\"alt_up_limit\":1024,\"alt_dl_limit\":1024}");
        }, timeout.Token);
        try
        {
            using var client = new QBittorrentClient(new(url, "user", "p&=+", ""));
            var limits = await client.GetLimitsAsync(timeout.Token);
            Assert.Equal(1024, limits.AlternativeUpload);
            await server;
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private static async Task Respond(HttpListenerContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
