using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.Entities;
using SegmentStitch.Merging;
using SegmentStitch.Sources;
using Xunit;

namespace SegmentStitch.Tests;

public class SourceTests
{
    private static long S(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    [Theory]
    [InlineData("Intro", MediaSegmentType.Intro)]
    [InlineData("  Opening Credits ", MediaSegmentType.Intro)]
    [InlineData("OP 2", MediaSegmentType.Intro)]
    [InlineData("Vorspann", MediaSegmentType.Intro)]
    [InlineData("Previously on Lost", MediaSegmentType.Recap)]
    [InlineData("ED", MediaSegmentType.Outro)]
    [InlineData("End Credits", MediaSegmentType.Outro)]
    [InlineData("Next Episode Preview", MediaSegmentType.Preview)]
    public void ChapterTitlesMarkTheirType(string title, MediaSegmentType type)
        => Assert.Equal(type, ChapterSource.TypeOf(title));

    [Theory]
    [InlineData("Headlines")]
    [InlineData("Opening Night")]
    [InlineData("Chapter 3")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryChapterTitlesMarkNothing(string? title)
        => Assert.Null(ChapterSource.TypeOf(title));

    [Fact]
    public void ChaptersRunToTheNextChapterAndTheLastToTheRunTime()
    {
        var chapters = new[]
        {
            new ChapterInfo { Name = "Credits", StartPositionTicks = S(1300) },
            new ChapterInfo { Name = "Cold Open", StartPositionTicks = 0 },
            new ChapterInfo { Name = "Intro", StartPositionTicks = S(60) },
            new ChapterInfo { Name = "Part 1", StartPositionTicks = S(90) },
        };

        var spans = ChapterSource.GetSpans(chapters, S(1380));

        Assert.Equal(
            [
                new SourceSpan(SegmentSource.Chapters, MediaSegmentType.Intro, S(60), S(90)),
                new SourceSpan(SegmentSource.Chapters, MediaSegmentType.Outro, S(1300), S(1380)),
            ],
            spans);
    }

    [Fact]
    public void ASingleChapterGivesNothing()
        => Assert.Empty(ChapterSource.GetSpans([new ChapterInfo { Name = "Intro", StartPositionTicks = 0 }], S(1380)));

    [Fact]
    public void IntroDbAnswerBecomesSpans()
    {
        // Shape of a real answer (Breaking Bad S01E01), plus an intro.
        const string Json = """
            {"imdb_id":"tt0903747","media_type":"tv","is_movie":false,"season":1,"episode":1,
             "intro":{"start_sec":120,"end_sec":135,"start_ms":120000,"end_ms":135000,"confidence":1,"submission_count":2},
             "recap":null,
             "outro":{"start_sec":3431,"end_sec":3500,"start_ms":3431000,"end_ms":3500000,"confidence":1,"submission_count":1},
             "post_credits":null,"no_intro":false,"no_recap":true,"no_outro":false}
            """;

        var spans = IntroDbClient.ToSpans(JsonSerializer.Deserialize<IntroDbClient.IntroDbResponse>(Json)!);

        Assert.Equal(
            [
                new SourceSpan(SegmentSource.IntroDb, MediaSegmentType.Intro, S(120), S(135)),
                new SourceSpan(SegmentSource.IntroDb, MediaSegmentType.Outro, S(3431), S(3500)),
            ],
            spans);
    }

    [Fact]
    public void IntroDbCreditsStopAtAPostCreditsScene()
    {
        var spans = IntroDbClient.ToSpans(new IntroDbClient.IntroDbResponse
        {
            Outro = new IntroDbClient.IntroDbTiming { StartMs = 6000_000, EndMs = 6400_000 },
            PostCredits = new IntroDbClient.IntroDbTiming { StartMs = 6200_000, EndMs = 6300_000 },
        });

        var credits = Assert.Single(spans);
        Assert.Equal((S(6000), S(6200)), (credits.StartTicks, credits.EndTicks));
    }

    [Fact]
    public void IntroDbCreditsStartAfterAPostCreditsSceneAtTheirStart()
    {
        var spans = IntroDbClient.ToSpans(new IntroDbClient.IntroDbResponse
        {
            Outro = new IntroDbClient.IntroDbTiming { StartMs = 6000_000, EndMs = 6400_000 },
            PostCredits = new IntroDbClient.IntroDbTiming { StartMs = 5990_000, EndMs = 6100_000 },
        });

        var credits = Assert.Single(spans);
        Assert.Equal((S(6100), S(6400)), (credits.StartTicks, credits.EndTicks));
    }

    [Fact]
    public void IntroDbEmptyAnswerGivesNothing()
        => Assert.Empty(IntroDbClient.ToSpans(new IntroDbClient.IntroDbResponse()));

    [Fact]
    public void TheIntroDbQueryUsesTheTvdbIdWithOrderAndEpisodeId()
        => Assert.Equal(
            "?tvdb_id=73871&season=7&episode=1&mapping=alternate&tvdb_episode_id=123&duration_ms=1320000",
            TheIntroDbClient.BuildMediaQuery(new TheIntroDbQuery(73871, 615, "tt0149460", false, 7, 1, "alternate", 123, 1320000)));

    [Fact]
    public void TheIntroDbQueryWithoutTvdbIdSendsNoOrder()
        => Assert.Equal(
            "?tmdb_id=615&season=7&episode=1",
            TheIntroDbClient.BuildMediaQuery(new TheIntroDbQuery(null, 615, null, false, 7, 1, "alternate", 123, null)));

    [Fact]
    public void FoundWithoutSpansIsNone()
        => Assert.Equal(LookupStatus.None, SourceLookup.Found([]).Status);

    [Fact]
    public void TheIntroDbTimestampZeroZeroIsNoSegment()
        => Assert.False(new TheIntroDbSegmentTimestamp { StartMs = 0, EndMs = 0 }.HasValidRange(true));
}
