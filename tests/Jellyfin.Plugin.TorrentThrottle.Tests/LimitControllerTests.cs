using Jellyfin.Plugin.TorrentThrottle.Clients;
using Jellyfin.Plugin.TorrentThrottle.Throttling;

namespace Jellyfin.Plugin.TorrentThrottle.Tests;

public class LimitControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "torrent-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();
    private readonly ConnectionOptions _connection = new("http://localhost:8080/qbit", "admin", "secret", "");
    private LimitRecoveryStore Store => new(Path.Combine(_directory, "limits.json"));
    private LimitController Controller() => new(new FakeFactory(_client), Store);

    [Fact]
    public async Task PreservesBothProfilesAndDoesNotTouchUnmanagedDownloads()
    {
        using var controller = Controller();
        await controller.ApplyAsync(_connection, new(10240, null), CancellationToken.None);
        Assert.Equal(new SpeedLimits(10240, 2000, 10240, 4000), _client.Current);
        await controller.RestoreAsync(_connection, CancellationToken.None);
        Assert.Equal(new SpeedLimits(0, 2000, 3000, 4000), _client.Current);
        Assert.Null(Store.Load());
    }

    [Fact]
    public async Task RepeatedDesiredCapsDoNotWriteAgain()
    {
        using var controller = Controller();
        await controller.ApplyAsync(_connection, new(10240, null), CancellationToken.None);
        await controller.ApplyAsync(_connection, new(10240, null), CancellationToken.None);
        Assert.Single(_client.Writes);
    }

    [Fact]
    public async Task CrashRecoveryRetainsOriginalLimitsInsteadOfCapturingReducedOnes()
    {
        using (var first = Controller())
        {
            await first.ApplyAsync(_connection, new(1024, 2048), CancellationToken.None);
        }

        using var next = Controller();
        await next.ApplyAsync(_connection, new(4096, 4096), CancellationToken.None);
        await next.RestoreAsync(_connection, CancellationToken.None);
        Assert.Equal(new SpeedLimits(0, 2000, 3000, 4000), _client.Current);
    }

    [Fact]
    public async Task DisablingDownloadRestoresItsOriginalSettingsWhileUploadRemainsManaged()
    {
        using var controller = Controller();
        await controller.ApplyAsync(_connection, new(1024, 2048), CancellationToken.None);
        await controller.ApplyAsync(_connection, new(4096, null), CancellationToken.None);
        Assert.Equal(new SpeedLimits(4096, 2000, 4096, 4000), _client.Current);
        Assert.False(Store.Load()!.DownloadOwned);
    }

    [Fact]
    public async Task TakingDownloadOwnershipLaterCapturesTheThenCurrentLimits()
    {
        using var controller = Controller();
        await controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None);
        _client.Current = _client.Current with { Download = 9000, AlternativeDownload = 10000 };
        await controller.ApplyAsync(_connection, new(1024, 2048), CancellationToken.None);
        await controller.RestoreAsync(_connection, CancellationToken.None);
        Assert.Equal(new SpeedLimits(0, 9000, 3000, 10000), _client.Current);
    }

    [Fact]
    public async Task FailedMutationKeepsRecoveryAndIsRetried()
    {
        using var controller = Controller();
        _client.FailWrites = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None));
        Assert.NotNull(Store.Load());
        _client.FailWrites = false;
        await controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None);
        Assert.Equal(1024, _client.Current.Upload);
    }

    [Fact]
    public async Task FailedRestoreKeepsRecoveryUntilSuccessfulRetry()
    {
        using var controller = Controller();
        await controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None);
        _client.FailWrites = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => controller.RestoreAsync(_connection, CancellationToken.None));
        Assert.NotNull(Store.Load());
        _client.FailWrites = false;
        await controller.RestoreAsync(_connection, CancellationToken.None);
        Assert.Null(Store.Load());
    }

    [Fact]
    public async Task DifferentEndpointCannotConsumeAnotherServersRecovery()
    {
        using (var controller = Controller())
        {
            await controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None);
        }

        using var next = Controller();
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.ApplyAsync(new("http://other:8080", "", "", "key"), new(2048, null), CancellationToken.None));
        Assert.Single(_client.Writes);
    }

    [Fact]
    public async Task FailedSnapshotWriteMustPreventAllMutationsIncludingRetries()
    {
        Directory.CreateDirectory(_directory);
        var blockingFile = Path.Combine(_directory, "block");
        File.WriteAllText(blockingFile, "not a directory");
        using var controller = new LimitController(new FakeFactory(_client), new LimitRecoveryStore(Path.Combine(blockingFile, "limits.json")));
        await Assert.ThrowsAnyAsync<IOException>(() => controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None));
        await Assert.ThrowsAnyAsync<IOException>(() => controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None));
        Assert.Empty(_client.Writes);
    }

    [Fact]
    public async Task SwitchingEndpointAfterFailedInitialReadUsesNewClient()
    {
        var factory = new SwitchingFactory();
        using var controller = new LimitController(factory, Store);
        await Assert.ThrowsAsync<HttpRequestException>(() => controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None));
        var next = new ConnectionOptions("http://new-server:8080", "", "", "key");
        await controller.ApplyAsync(next, new(2048, null), CancellationToken.None);
        Assert.Equal(2, factory.Created);
        Assert.Equal(2048, factory.Working.Current.Upload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingRejectedForeignRecoveryUnblocksWithoutRestart(bool restoreFirst)
    {
        Store.Save(new LimitRecovery("http://old-server:8080/", new(100, 200, 300, 400), false));
        using var controller = Controller();
        if (restoreFirst)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RestoreAsync(_connection, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None));
        }

        Assert.Empty(_client.Writes);
        Store.Clear();
        await controller.RestoreAsync(_connection, CancellationToken.None);
        await controller.ApplyAsync(_connection, new(2048, null), CancellationToken.None);
        Assert.Equal(2048, _client.Current.Upload);
        Assert.Equal(_connection.BaseUri.AbsoluteUri, Store.Load()!.Endpoint);
    }

    [Fact]
    public async Task RepairingRejectedRecoveryIsReadOnNextApply()
    {
        Store.Save(new LimitRecovery("http://old-server:8080/", new(100, 200, 300, 400), false));
        using var controller = Controller();
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None));
        var original = new SpeedLimits(1000, 2000, 3000, 4000);
        Store.Save(new LimitRecovery(_connection.BaseUri.AbsoluteUri, original, true));
        await controller.ApplyAsync(_connection, new(2048, 4096), CancellationToken.None);
        await controller.RestoreAsync(_connection, CancellationToken.None);
        Assert.Equal(original, _client.Current);
    }

    [Fact]
    public async Task SwitchingAwayFromAnUnreachableServerNamesItUntilTheRecordIsRemoved()
    {
        var replacement = new FakeClient();
        using var controller = new LimitController(new UrlFactory(_client, replacement), Store);
        await controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None);
        _client.FailWrites = true;
        var next = new ConnectionOptions("http://new-server:8080", "", "", "key");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ApplyAsync(next, new(2048, null), CancellationToken.None));
        Assert.Contains("previous Web UI URL", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ApplyAsync(next, new(2048, null), CancellationToken.None));
        Assert.Empty(replacement.Writes);

        Store.Clear();
        await controller.ApplyAsync(next, new(2048, null), CancellationToken.None);
        Assert.Equal(2048, replacement.Current.Upload);
        Assert.Equal(next.BaseUri.AbsoluteUri, Store.Load()!.Endpoint);
    }

    [Fact]
    public async Task ReachableServerIsRestoredFromMemoryEvenIfTheRecordWasRemoved()
    {
        var original = _client.Current;
        using var controller = Controller();
        await controller.ApplyAsync(_connection, new(1024, 2048), CancellationToken.None);
        Store.Clear();
        await controller.RestoreAsync(_connection, CancellationToken.None);
        Assert.Equal(original, _client.Current);
    }

    [Fact]
    public async Task UnreachableServerKeepsItsRecoveryUntilTheRecordIsRemoved()
    {
        using var controller = Controller();
        await controller.ApplyAsync(_connection, new(1024, null), CancellationToken.None);
        _client.FailWrites = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => controller.RestoreAsync(_connection, CancellationToken.None));
        Assert.NotNull(Store.Load());

        Store.Clear();
        await controller.RestoreAsync(_connection, CancellationToken.None);
        _client.FailWrites = false;
        await controller.RestoreAsync(_connection, CancellationToken.None);
        Assert.Single(_client.Writes);
    }

    private sealed class UrlFactory(FakeClient current, FakeClient replacement) : ITorrentClientFactory
    {
        public ITorrentClient Create(ConnectionOptions options) => options.BaseUri.Host == "new-server" ? replacement : current;
    }

    private sealed class SwitchingFactory : ITorrentClientFactory
    {
        public int Created { get; private set; }
        public FakeClient Working { get; } = new();
        public ITorrentClient Create(ConnectionOptions options) => ++Created == 1 ? new UnreachableClient() : Working;
    }

    private sealed class UnreachableClient : ITorrentClient
    {
        public Task<SpeedLimits> GetLimitsAsync(CancellationToken cancellationToken) => throw new HttpRequestException("offline");
        public Task SetLimitsAsync(long upload, long alternativeUpload, long? download, long? alternativeDownload, CancellationToken cancellationToken) => throw new InvalidOperationException("Wrong server!");
        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
    }

    internal sealed class FakeFactory(FakeClient client) : ITorrentClientFactory
    {
        public ITorrentClient Create(ConnectionOptions options) => client;
    }

    internal sealed class FakeClient : ITorrentClient
    {
        public SpeedLimits Current { get; set; } = new(0, 2000, 3000, 4000);
        public List<SpeedLimits> Writes { get; } = new();
        public bool FailWrites { get; set; }
        public int Reads { get; private set; }
        public Task<SpeedLimits> GetLimitsAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Current);
        }
        public Task SetLimitsAsync(long upload, long alternativeUpload, long? download, long? alternativeDownload, CancellationToken cancellationToken)
        {
            if (FailWrites) { throw new HttpRequestException("offline"); }
            Current = new(upload, download ?? Current.Download, alternativeUpload, alternativeDownload ?? Current.AlternativeDownload);
            Writes.Add(Current);
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
