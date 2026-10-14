using Jellyfin.Plugin.TorrentThrottle.Clients;
using Jellyfin.Plugin.TorrentThrottle.Throttling;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TorrentThrottle;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ITorrentClientFactory, TorrentClientFactory>();
        serviceCollection.AddSingleton<LimitRecoveryStore>();
        serviceCollection.AddSingleton<LimitController>();
        serviceCollection.AddSingleton<ThrottleStatus>();
        serviceCollection.AddHostedService<PlaybackMonitor>();
    }
}
