using System;
using System.Collections.Generic;
using SegmentStitch.Merging;

namespace SegmentStitch.Sources;

/// <summary>
/// What a source answered for an item.
/// </summary>
public sealed class SourceLookup
{
    private SourceLookup(LookupStatus status, IReadOnlyList<SourceSpan> spans)
    {
        Status = status;
        Spans = spans;
    }

    /// <summary>
    /// Gets how the lookup went.
    /// </summary>
    public LookupStatus Status { get; }

    /// <summary>
    /// Gets the segments found, empty unless <see cref="Status"/> is <see cref="LookupStatus.Found"/>.
    /// </summary>
    public IReadOnlyList<SourceSpan> Spans { get; }

    /// <summary>
    /// The source has segments for the item.
    /// </summary>
    /// <param name="spans">The segments.</param>
    /// <returns>The lookup.</returns>
    public static SourceLookup Found(IReadOnlyList<SourceSpan> spans)
        => spans.Count == 0 ? None() : new(LookupStatus.Found, spans);

    /// <summary>
    /// The source has nothing for the item, or cannot be asked for it.
    /// </summary>
    /// <returns>The lookup.</returns>
    public static SourceLookup None() => new(LookupStatus.None, Array.Empty<SourceSpan>());

    /// <summary>
    /// The source could not be asked or gave no usable answer; ask again later.
    /// </summary>
    /// <returns>The lookup.</returns>
    public static SourceLookup Failed() => new(LookupStatus.Failed, Array.Empty<SourceSpan>());
}
