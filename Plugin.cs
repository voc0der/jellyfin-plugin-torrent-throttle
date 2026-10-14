using Jellyfin.Plugin.TorrentThrottle.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TorrentThrottle;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }
    internal static Guid PluginId { get; } = Guid.Parse("9f276cce-98c3-4554-b495-61cf673cf34c");
    public override Guid Id => PluginId;
    public override string Name => "Torrent Throttle";
    public override string Description => "Reserves bandwidth for remote Jellyfin playback by adjusting torrent speed limits.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo { Name = Name, EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" }
    ];

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // Allow disabling even if the saved connection or caps need repair.
        if (configuration is PluginConfiguration { Enabled: true } settings)
        {
            settings.Validate();
        }

        base.UpdateConfiguration(configuration);
    }
}
