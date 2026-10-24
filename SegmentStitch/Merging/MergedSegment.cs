using System.Collections.Generic;

namespace SegmentStitch.Merging;

/// <summary>
/// A merged segment and the sources it was merged from.
/// </summary>
/// <param name="StartTicks">Start in ticks.</param>
/// <param name="EndTicks">End in ticks.</param>
/// <param name="From">The sources whose time it is.</param>
/// <param name="LeftOut">The sources that had the type but disagreed, or lost by priority.</param>
public sealed record MergedSegment(long StartTicks, long EndTicks, IReadOnlyList<SegmentSource> From, IReadOnlyList<SegmentSource> LeftOut);
