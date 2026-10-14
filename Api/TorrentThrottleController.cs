using Jellyfin.Plugin.TorrentThrottle.Clients;
using Jellyfin.Plugin.TorrentThrottle.Configuration;
using Jellyfin.Plugin.TorrentThrottle.Throttling;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrentThrottle.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("TorrentThrottle")]
public sealed class TorrentThrottleController(ThrottleStatus status, ITorrentClientFactory factory) : ControllerBase
{
    [HttpGet("Status")]
    public ActionResult<ThrottleSnapshot> GetStatus() => status.Current;

    [HttpPost("TestConnection")]
    public async Task<ActionResult> TestConnection([FromBody] PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            // This endpoint never changes torrent preferences or saved config.
            using var client = factory.Create(configuration.GetConnection());
            await client.GetLimitsAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { Success = true, Message = "Connected to qBittorrent. Current speed limits are readable." });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            return BadRequest(new { Success = false, Message = PlaybackMonitor.FailureMessage(ex) });
        }
    }
}
