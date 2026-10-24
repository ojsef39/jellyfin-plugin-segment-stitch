namespace SegmentStitch.Merging;

/// <summary>
/// Where a segment time comes from.
/// </summary>
public enum SegmentSource
{
    /// <summary>
    /// TheIntroDB (api.theintrodb.org).
    /// </summary>
    TheIntroDb,

    /// <summary>
    /// IntroDB (api.introdb.app).
    /// </summary>
    IntroDb,

    /// <summary>
    /// The item's named chapters.
    /// </summary>
    Chapters,
}
