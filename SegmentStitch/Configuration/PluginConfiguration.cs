using MediaBrowser.Model.Plugins;
using SegmentStitch.Merging;

namespace SegmentStitch.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether TheIntroDB is asked.
    /// </summary>
    public bool EnableTheIntroDb { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether IntroDB is asked.
    /// </summary>
    public bool EnableIntroDb { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the item's named chapters are read.
    /// </summary>
    public bool EnableChapters { get; set; } = true;

    /// <summary>
    /// Gets or sets the optional TheIntroDB API key; your own submissions are weighted higher with it.
    /// </summary>
    public string TheIntroDbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether intro segments are provided.
    /// </summary>
    public bool EnableIntro { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether recap segments are provided.
    /// </summary>
    public bool EnableRecap { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether credits segments are provided.
    /// </summary>
    public bool EnableCredits { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether preview segments are provided.
    /// </summary>
    public bool EnablePreview { get; set; } = true;

    /// <summary>
    /// Gets or sets how several sources' times for one segment type are merged.
    /// </summary>
    public MergeMode MergeMode { get; set; } = MergeMode.Smart;

    /// <summary>
    /// Gets or sets the sources in priority order, comma separated: the order
    /// <see cref="MergeMode.Priority"/> picks by, and the tie-break of <see cref="MergeMode.Smart"/>.
    /// </summary>
    public string SourcePriority { get; set; } = "TheIntroDb,IntroDb,Chapters";

    /// <summary>
    /// Gets or sets after how many days an item's segments are fetched again, so corrections and
    /// new submissions arrive. 0 never refreshes them.
    /// </summary>
    public int RefreshAfterDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets a value indicating whether items are looked up when added, after a metadata
    /// download and on playback, instead of on the next media segment scan.
    /// </summary>
    public bool EnableOnDemandFetch { get; set; } = true;
}
