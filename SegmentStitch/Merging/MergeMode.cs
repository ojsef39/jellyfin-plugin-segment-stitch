namespace SegmentStitch.Merging;

/// <summary>
/// How the times of several sources for one segment type become one segment.
/// </summary>
public enum MergeMode
{
    /// <summary>
    /// The time the most sources agree on: from the latest start to the earliest end. A source
    /// that overlaps none of the others is left out; when no two sources overlap, the first by
    /// priority is taken.
    /// </summary>
    Smart,

    /// <summary>
    /// The first source in the priority order that has the type.
    /// </summary>
    Priority,
}
