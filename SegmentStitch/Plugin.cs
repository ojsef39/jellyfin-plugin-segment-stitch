using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using SegmentStitch.Configuration;

namespace SegmentStitch;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        RecentFetches = new LookupCache(Path.Combine(DataFolderPath, "recent-fetches.json"));
        TheIntroDbMisses = new LookupCache(Path.Combine(DataFolderPath, "theintrodb-misses.json"));
        IntroDbMisses = new LookupCache(Path.Combine(DataFolderPath, "introdb-misses.json"));
    }

    /// <inheritdoc />
    public override string Name => "Segment Stitch";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("926de832-7fb9-4af6-a1f3-c32b2b4cfc4a");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the items fetched lately, which are not fetched again before
    /// <see cref="PluginConfiguration.RefreshAfterDays"/> passed.
    /// </summary>
    public LookupCache RecentFetches { get; }

    /// <summary>
    /// Gets the lookups TheIntroDB had nothing for lately.
    /// </summary>
    public LookupCache TheIntroDbMisses { get; }

    /// <summary>
    /// Gets the lookups IntroDB had nothing for lately.
    /// </summary>
    public LookupCache IntroDbMisses { get; }

    /// <summary>
    /// Writes all caches to disk.
    /// </summary>
    public void SaveCaches()
    {
        RecentFetches.Save();
        TheIntroDbMisses.Save();
        IntroDbMisses.Save();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuIcon = "call_merge",
            },
        ];
    }
}
