using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Jellyfin.Plugin.TorrentThrottle.Clients;

namespace Jellyfin.Plugin.TorrentThrottle.Tests;

public class CertificateValidationTests
{
    [Theory]
    [InlineData("https://172.30.12.103/", true)]
    [InlineData("https://172.16.0.1:8443/", true)]
    [InlineData("https://10.1.2.3/", true)]
    [InlineData("https://192.168.1.20:8080/", true)]
    [InlineData("https://127.0.0.1/", true)]
    [InlineData("https://169.254.10.10/", true)]
    [InlineData("https://[::1]/", true)]
    [InlineData("https://[fd00::20]/", true)]
    [InlineData("https://[fe80::1]/", true)]
    [InlineData("https://[::ffff:192.168.1.20]/", true)]
    [InlineData("https://172.32.0.1/", false)]
    [InlineData("https://172.15.255.255/", false)]
    [InlineData("https://8.8.8.8/", false)]
    [InlineData("https://[2001:db8::1]/", false)]
    [InlineData("https://localhost/", false)]
    [InlineData("https://qbittorrent.example.net/", false)]
    public void OnlyPrivateAndLoopbackAddressesSkipValidation(string url, bool expected) =>
        Assert.Equal(expected, QBittorrentClient.IsPrivateAddress(new Uri(url)));

    [Fact]
    public async Task SelfSignedCertificateIsAcceptedOnAPrivateAddress()
    {
        using var certificate = CreateSelfSignedCertificate();
        var (listener, port) = Listen();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeOnce(listener, certificate, timeout.Token);
        try
        {
            using var client = new QBittorrentClient(new($"https://127.0.0.1:{port}/", "", "", "key"));
            var limits = await client.GetLimitsAsync(timeout.Token);
            Assert.Equal(2048, limits.Upload);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    [Fact]
    public async Task SelfSignedCertificateIsRejectedForAHostName()
    {
        using var certificate = CreateSelfSignedCertificate();
        var (listener, port) = Listen();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeOnce(listener, certificate, timeout.Token);
        try
        {
            using var client = new QBittorrentClient(new($"https://localhost:{port}/", "", "", "key"));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLimitsAsync(timeout.Token));
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=qbittorrent.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Reload so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private static Task ServeOnce(TcpListener listener, X509Certificate2 certificate, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var tls = new SslStream(socket.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellationToken);
            var buffer = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await tls.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            Assert.StartsWith("GET /api/v2/app/preferences ", request.ToString(), StringComparison.Ordinal);
            var body = "{\"up_limit\":2048,\"dl_limit\":0,\"alt_up_limit\":0,\"alt_dl_limit\":0}";
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await tls.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The rejecting client aborts the handshake; the test asserts on the client side.
        }
    });
}
