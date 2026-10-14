using System.Text.Json;
using Jellyfin.Plugin.TorrentThrottle.Api;
using Jellyfin.Plugin.TorrentThrottle.Clients;
using Jellyfin.Plugin.TorrentThrottle.Configuration;
using Jellyfin.Plugin.TorrentThrottle.Throttling;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrentThrottle.Tests;

public class TorrentThrottleControllerTests
{
    [Fact]
    public void StatusReturnsTheLatestSnapshotWithoutConnecting()
    {
        var status = new ThrottleStatus();
        var factory = new RecordingFactory();
        var controller = new TorrentThrottleController(status, factory);
        Assert.Equal(new ThrottleSnapshot("Disabled"), controller.GetStatus().Value);

        var snapshot = new ThrottleSnapshot("Throttling", 2, 1, 1000000, 2048, 4096);
        status.Set(snapshot);
        Assert.Same(snapshot, controller.GetStatus().Value);
        Assert.Null(factory.Options);
    }

    [Fact]
    public async Task ConnectionTestReadsLimitsUsingSubmittedSettingsAndDisposesTheClient()
    {
        var configuration = new PluginConfiguration
        {
            WebUiUrl = " https://example.test/qbit ",
            Username = "user",
            Password = "password",
            ApiKey = " key "
        };
        var original = JsonSerializer.Serialize(configuration);
        var factory = new RecordingFactory();
        var controller = new TorrentThrottleController(new ThrottleStatus(), factory);
        using var cancellation = new CancellationTokenSource();

        var result = Assert.IsType<OkObjectResult>(await controller.TestConnection(configuration, cancellation.Token));

        AssertResponse(result, true, "Connected to qBittorrent. Current speed limits are readable.");
        Assert.Equal(configuration.GetConnection(), factory.Options);
        Assert.Equal(cancellation.Token, factory.Client.Token);
        Assert.Equal(1, factory.Client.Reads);
        Assert.True(factory.Client.Disposed);
        Assert.Equal(original, JsonSerializer.Serialize(configuration));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException), "Check the saved-limit recovery file.", "Check the saved-limit recovery file.")]
    [InlineData(typeof(ArgumentException), "private connection details", "Invalid connection or bandwidth settings. Check the saved configuration.")]
    [InlineData(typeof(HttpRequestException), "private request details", "The qBittorrent Web API request failed. Check network access, the URL, credentials and Web UI permissions.")]
    [InlineData(typeof(OperationCanceledException), "private timeout details", "The qBittorrent Web API request timed out. Check network access from Jellyfin.")]
    [InlineData(typeof(IOException), "private file path", "Could not update or restore torrent limits. Check Jellyfin's log and the saved-limit recovery file.")]
    public async Task ConnectionFailuresReturnActionableErrorsAndDisposeTheClient(Type failureType, string details, string expected)
    {
        var factory = new RecordingFactory();
        factory.Client.Failure = (Exception)Activator.CreateInstance(failureType, details)!;
        var controller = new TorrentThrottleController(new ThrottleStatus(), factory);

        var result = Assert.IsType<BadRequestObjectResult>(await controller.TestConnection(new PluginConfiguration(), CancellationToken.None));

        AssertResponse(result, false, expected);
        Assert.Equal(1, factory.Client.Reads);
        Assert.True(factory.Client.Disposed);
    }

    [Fact]
    public async Task InvalidUrlIsRejectedBeforeCreatingAClient()
    {
        var factory = new RecordingFactory();
        var controller = new TorrentThrottleController(new ThrottleStatus(), factory);

        var result = Assert.IsType<BadRequestObjectResult>(await controller.TestConnection(
            new PluginConfiguration { WebUiUrl = "ftp://example.test" }, CancellationToken.None));

        AssertResponse(result, false, "Invalid connection or bandwidth settings. Check the saved configuration.");
        Assert.Null(factory.Options);
        Assert.Equal(0, factory.Client.Reads);
    }

    [Fact]
    public async Task RequestCancellationPropagatesAndDisposesTheClient()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new RecordingFactory();
        var failure = new OperationCanceledException(cancellation.Token);
        factory.Client.Failure = failure;
        var controller = new TorrentThrottleController(new ThrottleStatus(), factory);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => controller.TestConnection(new PluginConfiguration(), cancellation.Token));

        Assert.Same(failure, actual);
        Assert.Equal(cancellation.Token, factory.Client.Token);
        Assert.True(factory.Client.Disposed);
    }

    [Fact]
    public async Task FatalFailuresPropagateAndDisposeTheClient()
    {
        var factory = new RecordingFactory();
        var failure = new OutOfMemoryException();
        factory.Client.Failure = failure;
        var controller = new TorrentThrottleController(new ThrottleStatus(), factory);

        var actual = await Assert.ThrowsAsync<OutOfMemoryException>(() => controller.TestConnection(new PluginConfiguration(), CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.True(factory.Client.Disposed);
    }

    private static void AssertResponse(ObjectResult result, bool success, string message)
    {
        var response = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(success, response.GetProperty("Success").GetBoolean());
        Assert.Equal(message, response.GetProperty("Message").GetString());
    }

    private sealed class RecordingFactory : ITorrentClientFactory
    {
        public RecordingClient Client { get; } = new();
        public ConnectionOptions? Options { get; private set; }

        public ITorrentClient Create(ConnectionOptions options)
        {
            Options = options;
            return Client;
        }
    }

    private sealed class RecordingClient : ITorrentClient
    {
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool Disposed { get; private set; }

        public Task<SpeedLimits> GetLimitsAsync(CancellationToken cancellationToken)
        {
            Reads++;
            Token = cancellationToken;
            return Failure == null ? Task.FromResult(new SpeedLimits(0, 2000, 3000, 4000)) : Task.FromException<SpeedLimits>(Failure);
        }

        public Task SetLimitsAsync(long upload, long alternativeUpload, long? download, long? alternativeDownload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Connection tests must never change speed limits.");

        public void Dispose() => Disposed = true;
    }
}
