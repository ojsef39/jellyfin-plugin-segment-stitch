using Jellyfin.Database.Implementations.Enums;

namespace SegmentStitch.Merging;

/// <summary>
/// One segment as a source gives it.
/// </summary>
/// <param name="Source">Where it comes from.</param>
/// <param name="Type">The segment type.</param>
/// <param name="StartTicks">Start in ticks.</param>
/// <param name="EndTicks">End in ticks.</param>
public sealed record SourceSpan(SegmentSource Source, MediaSegmentType Type, long StartTicks, long EndTicks);
