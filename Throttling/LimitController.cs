using Jellyfin.Plugin.TorrentThrottle.Clients;

namespace Jellyfin.Plugin.TorrentThrottle.Throttling;

// Owned exclusively by the background worker. A recovery record is persisted
// before any mutation, including taking ownership of download limits later.
public sealed class LimitController(ITorrentClientFactory factory, LimitRecoveryStore store) : IDisposable
{
    private ITorrentClient? _client;
    private ConnectionOptions? _connection;
    private LimitRecovery? _recovery;
    private DesiredLimits? _applied;

    public async Task ApplyAsync(ConnectionOptions connection, DesiredLimits desired, CancellationToken cancellationToken)
    {
        await ConnectAsync(connection, cancellationToken).ConfigureAwait(false);
        if (_recovery == null)
        {
            _recovery = new LimitRecovery(connection.BaseUri.AbsoluteUri, await _client!.GetLimitsAsync(cancellationToken).ConfigureAwait(false), false);
            store.Save(_recovery);
        }

        if (_applied == desired)
        {
            return;
        }

        if (desired.Download.HasValue && !_recovery.DownloadOwned)
        {
            // Downloads may have changed while only upload was managed.
            var current = await _client!.GetLimitsAsync(cancellationToken).ConfigureAwait(false);
            _recovery = _recovery with
            {
                Original = _recovery.Original with { Download = current.Download, AlternativeDownload = current.AlternativeDownload },
                DownloadOwned = true
            };
            store.Save(_recovery);
        }

        var releaseDownload = !desired.Download.HasValue && _recovery.DownloadOwned;
        // Also repeat this after a failed disk write: in-memory ownership alone
        // must never allow a request to change limits without a durable record.
        store.Save(_recovery);
        await _client!.SetLimitsAsync(desired.Upload, desired.Upload,
            desired.Download ?? (releaseDownload ? _recovery.Original.Download : null),
            desired.Download ?? (releaseDownload ? _recovery.Original.AlternativeDownload : null), cancellationToken).ConfigureAwait(false);

        if (releaseDownload)
        {
            _recovery = _recovery with { DownloadOwned = false };
            store.Save(_recovery);
        }

        _applied = desired;
    }

    private async Task ConnectAsync(ConnectionOptions connection, CancellationToken cancellationToken)
    {
        if (_connection != null && _connection != connection)
        {
            if (_connection.BaseUri != connection.BaseUri)
            {
                // Do not strand reduced caps on the previous server.
                try
                {
                    await RestoreAsync(null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
                {
                    // Shown on the settings page. A generic request failure would
                    // point at the URL just entered, not the server that failed.
                    throw new InvalidOperationException("Could not restore the saved limits on the previous Web UI URL. Bring that server back online, or set its limits manually and delete plugins/configurations/TorrentThrottle.limits.json.", ex);
                }
            }
            else
            {
                // Credential rotation on the same server keeps its saved limits.
                _client?.Dispose();
                _client = null;
                _applied = null;
            }
        }

        if (_client != null)
        {
            return;
        }

        _recovery ??= ReadRecovery(connection);

        _connection = connection;
        _client = factory.Create(connection);
    }

    public async Task RestoreAsync(ConnectionOptions? connection, CancellationToken cancellationToken)
    {
        _recovery ??= ReadRecovery(_connection ?? connection);
        if (_recovery == null)
        {
            Release();
            return;
        }

        try
        {
            if (_client == null)
            {
                if (connection == null)
                {
                    throw new InvalidOperationException("Connect to the saved Web UI URL to restore torrent limits.");
                }

                await ConnectAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            else if (connection != null && _connection?.BaseUri == connection.BaseUri && _connection != connection)
            {
                await ConnectAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            var original = _recovery.Original;
            await _client!.SetLimitsAsync(original.Upload, original.AlternativeUpload,
                _recovery.DownloadOwned ? original.Download : null,
                _recovery.DownloadOwned ? original.AlternativeDownload : null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested && store.WasRemoved())
        {
            // Limits only change after the record is saved, so a missing file
            // means an administrator removed it after setting these limits
            // manually. Stop retrying a server that cannot restore them.
            Release();
            return;
        }

        store.Clear();
        Release();
    }

    public void Dispose() => _client?.Dispose();

    private void Release()
    {
        _client?.Dispose();
        _client = null;
        _connection = null;
        _recovery = null;
        _applied = null;
    }

    private LimitRecovery? ReadRecovery(ConnectionOptions? connection)
    {
        var saved = store.Load();
        if (saved != null)
        {
            if (connection == null)
            {
                throw new InvalidOperationException("Connect to the saved Web UI URL to restore torrent limits.");
            }

            if (saved.Endpoint != connection.BaseUri.AbsoluteUri)
            {
                // Do not cache a rejected record. If an administrator repairs
                // or removes it, the next retry must read the file again.
                throw new InvalidOperationException("Saved limits belong to a different Web UI URL. Reconnect to that URL and disable throttling to restore them before switching servers.");
            }
        }

        return saved;
    }
}
