namespace SegmentStitch.Sources;

/// <summary>
/// How a source lookup went.
/// </summary>
public enum LookupStatus
{
    /// <summary>
    /// The source has segments for the item.
    /// </summary>
    Found,

    /// <summary>
    /// The source has nothing for the item, or cannot be asked for it.
    /// </summary>
    None,

    /// <summary>
    /// The source could not be asked or gave no usable answer.
    /// </summary>
    Failed,
}
