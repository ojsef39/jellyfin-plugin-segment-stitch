using System;
using Jellyfin.Database.Implementations.Enums;
using SegmentStitch.Merging;
using Xunit;

namespace SegmentStitch.Tests;

public class SegmentMergerTests
{
    private static readonly SegmentSource[] DefaultPriority = [SegmentSource.TheIntroDb, SegmentSource.IntroDb, SegmentSource.Chapters];

    private static SourceSpan Span(SegmentSource source, double start, double end, MediaSegmentType type = MediaSegmentType.Intro)
        => new(source, type, TimeSpan.FromSeconds(start).Ticks, TimeSpan.FromSeconds(end).Ticks);

    private static (double Start, double End)? Merge(MergeMode mode, params SourceSpan[] spans)
        => Merge(mode, DefaultPriority, spans);

    private static (double Start, double End)? Merge(MergeMode mode, SegmentSource[] priority, params SourceSpan[] spans)
        => SegmentMerger.Merge(spans, MediaSegmentType.Intro, mode, priority) is { } m
            ? (TimeSpan.FromTicks(m.StartTicks).TotalSeconds, TimeSpan.FromTicks(m.EndTicks).TotalSeconds)
            : null;

    [Fact]
    public void NoSourceGivesNoSegment()
        => Assert.Null(Merge(MergeMode.Smart));

    [Fact]
    public void OneSourceIsTakenAsItIs()
        => Assert.Equal((60, 90), Merge(MergeMode.Smart, Span(SegmentSource.IntroDb, 60, 90)));

    [Fact]
    public void OverlappingSourcesGiveTheOverlap()
        => Assert.Equal((62, 88), Merge(MergeMode.Smart, Span(SegmentSource.TheIntroDb, 60, 88), Span(SegmentSource.IntroDb, 62, 95)));

    [Fact]
    public void TwoDisagreeingSourcesGiveTheFirstByPriority()
    {
        Assert.Equal((0, 30), Merge(MergeMode.Smart, Span(SegmentSource.TheIntroDb, 0, 30), Span(SegmentSource.IntroDb, 60, 90)));
        Assert.Equal((60, 90), Merge(
            MergeMode.Smart,
            [SegmentSource.IntroDb, SegmentSource.TheIntroDb],
            Span(SegmentSource.TheIntroDb, 0, 30),
            Span(SegmentSource.IntroDb, 60, 90)));
    }

    [Fact]
    public void ThreeOverlappingSourcesGiveTheirCommonOverlap()
        => Assert.Equal((62, 85), Merge(
            MergeMode.Smart,
            Span(SegmentSource.TheIntroDb, 60, 88),
            Span(SegmentSource.IntroDb, 62, 95),
            Span(SegmentSource.Chapters, 55, 85)));

    [Fact]
    public void ASourceOverlappingNoOtherIsLeftOut()
    {
        // A wrong "Intro" chapter (15 to 46 s) next to two databases that agree.
        Assert.Equal((62, 88), Merge(
            MergeMode.Smart,
            Span(SegmentSource.TheIntroDb, 60, 88),
            Span(SegmentSource.IntroDb, 62, 90),
            Span(SegmentSource.Chapters, 15, 46)));
    }

    [Fact]
    public void AmongPairsTheHigherPriorityPairWins()
    {
        // TheIntroDB and IntroDB do not overlap, chapters overlap both.
        Assert.Equal((20, 30), Merge(
            MergeMode.Smart,
            Span(SegmentSource.TheIntroDb, 0, 30),
            Span(SegmentSource.IntroDb, 40, 70),
            Span(SegmentSource.Chapters, 20, 50)));
    }

    [Fact]
    public void ThreePairwiseDisagreeingSourcesGiveTheFirstByPriority()
        => Assert.Equal((0, 10), Merge(
            MergeMode.Smart,
            Span(SegmentSource.TheIntroDb, 0, 10),
            Span(SegmentSource.IntroDb, 20, 30),
            Span(SegmentSource.Chapters, 40, 50)));

    [Fact]
    public void PriorityModeTakesTheFirstSourceThatHasTheType()
        => Assert.Equal((62, 95), Merge(
            MergeMode.Priority,
            [SegmentSource.IntroDb, SegmentSource.TheIntroDb],
            Span(SegmentSource.TheIntroDb, 60, 88),
            Span(SegmentSource.IntroDb, 62, 95)));

    [Fact]
    public void PriorityModeSkipsSourcesWithoutTheType()
        => Assert.Equal((60, 88), Merge(
            MergeMode.Priority,
            Span(SegmentSource.TheIntroDb, 60, 88),
            Span(SegmentSource.IntroDb, 1300, 1400, MediaSegmentType.Outro)));

    [Fact]
    public void ASourceTakesPartWithItsEarliestSegment()
        => Assert.Equal((62, 88), Merge(
            MergeMode.Smart,
            Span(SegmentSource.TheIntroDb, 600, 650),
            Span(SegmentSource.TheIntroDb, 60, 88),
            Span(SegmentSource.IntroDb, 62, 95)));

    [Fact]
    public void SourcesMissingFromThePriorityAreLeftOut()
        => Assert.Equal((60, 88), Merge(
            MergeMode.Smart,
            [SegmentSource.TheIntroDb],
            Span(SegmentSource.TheIntroDb, 60, 88),
            Span(SegmentSource.IntroDb, 62, 95)));

    [Fact]
    public void EmptySpansAreIgnored()
        => Assert.Equal((60, 88), Merge(MergeMode.Smart, Span(SegmentSource.TheIntroDb, 60, 88), Span(SegmentSource.IntroDb, 70, 70)));
}
