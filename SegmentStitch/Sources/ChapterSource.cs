using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.Entities;
using SegmentStitch.Merging;

namespace SegmentStitch.Sources;

/// <summary>
/// Segments from an item's named chapters, after Gelato's chapter titles.
/// </summary>
public static class ChapterSource
{
    /// <summary>
    /// Chapter titles that mark a segment, matched against the whole title, ignoring case, so
    /// "Headlines" is no ad break and "Opening Night" no intro.
    /// </summary>
    private static readonly (Regex Title, MediaSegmentType Type)[] ChapterTypes =
    [
        (
            ChapterTitle(@"intro|opening( credits| titles)?|op ?\d*|main titles?|title sequence|g[ée]n[ée]rique( de d[ée]but)?|vorspann|sigla( iniziale)?"),
            MediaSegmentType.Intro
        ),
        (
            ChapterTitle(@"recap|previously( on .*)?|r[ée]sum[ée]|r[üu]ckblick|riassunto"),
            MediaSegmentType.Recap
        ),
        (
            ChapterTitle(@"(end |closing )?credits|outro|ending( credits)?|ed ?\d*|end titles|g[ée]n[ée]rique de fin|abspann|titoli di coda|cr[ée]ditos( finales)?"),
            MediaSegmentType.Outro
        ),
        (
            ChapterTitle(@"preview|next episode( preview)?|next time|vorschau|anteprima"),
            MediaSegmentType.Preview
        ),
    ];

    /// <summary>
    /// A segment for each named chapter, from its start to the next chapter's (the runtime for
    /// the last one).
    /// </summary>
    /// <param name="chapters">The item's chapters.</param>
    /// <param name="runTimeTicks">The item's runtime, if known.</param>
    /// <returns>The segments; none for fewer than two chapters.</returns>
    public static IReadOnlyList<SourceSpan> GetSpans(IEnumerable<ChapterInfo> chapters, long? runTimeTicks)
    {
        var ordered = chapters.OrderBy(c => c.StartPositionTicks).ToList();
        var spans = new List<SourceSpan>();
        if (ordered.Count < 2)
        {
            return spans;
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (TypeOf(ordered[i].Name) is not { } type)
            {
                continue;
            }

            var end = i + 1 < ordered.Count ? ordered[i + 1].StartPositionTicks : runTimeTicks ?? 0;
            if (end > ordered[i].StartPositionTicks)
            {
                spans.Add(new SourceSpan(SegmentSource.Chapters, type, ordered[i].StartPositionTicks, end));
            }
        }

        return spans;
    }

    /// <summary>
    /// The segment type a chapter title marks.
    /// </summary>
    /// <param name="name">The chapter title.</param>
    /// <returns>The type, or null for an ordinary chapter.</returns>
    internal static MediaSegmentType? TypeOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var title = name.Trim();
        foreach (var (regex, type) in ChapterTypes)
        {
            if (regex.IsMatch(title))
            {
                return type;
            }
        }

        return null;
    }

    private static Regex ChapterTitle(string pattern)
        => new($"^(?:{pattern})$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
