using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;

namespace SegmentStitch.Merging;

/// <summary>
/// Merges the sources' segments into at most one segment per type.
/// </summary>
public static class SegmentMerger
{
    /// <summary>
    /// Merges the segments of one type.
    /// </summary>
    /// <remarks>
    /// A source with several segments of the type takes part with its earliest one.
    /// </remarks>
    /// <param name="spans">The sources' segments; only those of <paramref name="type"/> count.</param>
    /// <param name="type">The segment type to merge.</param>
    /// <param name="mode">How to merge.</param>
    /// <param name="priority">The sources in priority order; sources not listed are left out.</param>
    /// <returns>The merged segment, or null for none.</returns>
    public static MergedSegment? Merge(
        IEnumerable<SourceSpan> spans,
        MediaSegmentType type,
        MergeMode mode,
        IReadOnlyList<SegmentSource> priority)
    {
        var bySource = spans
            .Where(s => s.Type == type && s.EndTicks > s.StartTicks)
            .GroupBy(s => s.Source)
            .ToDictionary(g => g.Key, g => g.MinBy(s => s.StartTicks)!);
        var ordered = priority.Distinct().Where(bySource.ContainsKey).Select(s => bySource[s]).ToList();

        if (ordered.Count == 0)
        {
            return null;
        }

        if (mode == MergeMode.Priority || ordered.Count == 1)
        {
            return Taken([ordered[0]], ordered);
        }

        // Largest group of sources that share a time first; among groups of one size the one
        // with the higher priority sources.
        for (var size = ordered.Count; size >= 2; size--)
        {
            foreach (var group in Combinations(ordered, size))
            {
                var start = group.Max(s => s.StartTicks);
                var end = group.Min(s => s.EndTicks);
                if (end > start)
                {
                    return Taken(group, ordered) with { StartTicks = start, EndTicks = end };
                }
            }
        }

        // No two sources agree: the first by priority.
        return Taken([ordered[0]], ordered);
    }

    private static MergedSegment Taken(List<SourceSpan> used, List<SourceSpan> candidates)
        => new(
            used[0].StartTicks,
            used[0].EndTicks,
            used.Select(s => s.Source).ToList(),
            candidates.Except(used).Select(s => s.Source).ToList());

    /// <summary>
    /// The groups of <paramref name="size"/> items, in lexicographic order of their positions.
    /// </summary>
    private static IEnumerable<List<T>> Combinations<T>(List<T> items, int size)
    {
        var indices = Enumerable.Range(0, size).ToArray();
        while (true)
        {
            yield return indices.Select(i => items[i]).ToList();

            var pos = size - 1;
            while (pos >= 0 && indices[pos] == items.Count - size + pos)
            {
                pos--;
            }

            if (pos < 0)
            {
                yield break;
            }

            indices[pos]++;
            for (var i = pos + 1; i < size; i++)
            {
                indices[i] = indices[i - 1] + 1;
            }
        }
    }
}
