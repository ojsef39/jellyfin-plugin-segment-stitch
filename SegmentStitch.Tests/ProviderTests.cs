using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using SegmentStitch.Configuration;
using SegmentStitch.Merging;
using Xunit;

namespace SegmentStitch.Tests;

public class ProviderTests
{
    private static long S(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    [Theory]
    [InlineData(0, 0, true, false)] // nothing stored: fetch
    [InlineData(2, 2, false, true)] // own segments, not due: keep
    [InlineData(2, 2, true, false)] // own segments, due: fetch
    [InlineData(0, 3, true, true)] // another provider's segments served: keep
    public void KeepsOrFetches(int own, int served, bool refreshDue, bool keep)
        => Assert.Equal(keep, StitchSegmentProvider.ShouldKeepExistingSegments(own, served, refreshDue));

    [Theory]
    [InlineData(0.0, 22.5)]
    [InlineData(0.999999, 37.5)]
    public void RefreshIsSpreadAroundTheSetting(double jitter, double days)
        => Assert.Equal(days, StitchSegmentProvider.RefreshTtl(30, jitter).TotalDays, 3);

    [Fact]
    public void PriorityKeepsTheNamedOrderAndAddsTheRest()
        => Assert.Equal(
            [SegmentSource.Chapters, SegmentSource.TheIntroDb, SegmentSource.IntroDb],
            StitchSegmentProvider.ParsePriority(" chapters , nonsense, Chapters,theintrodb"));

    [Fact]
    public void EmptyPriorityIsTheDefaultOrder()
        => Assert.Equal(
            [SegmentSource.TheIntroDb, SegmentSource.IntroDb, SegmentSource.Chapters],
            StitchSegmentProvider.ParsePriority(null));

    [Fact]
    public void MergesEachEnabledType()
    {
        var spans = new List<SourceSpan>
        {
            new(SegmentSource.TheIntroDb, MediaSegmentType.Intro, S(60), S(90)),
            new(SegmentSource.IntroDb, MediaSegmentType.Intro, S(62), S(92)),
            new(SegmentSource.IntroDb, MediaSegmentType.Outro, S(1300), S(1380)),
            new(SegmentSource.Chapters, MediaSegmentType.Recap, S(0), S(30)),
        };
        var config = new PluginConfiguration { EnableRecap = false };

        var segments = StitchSegmentProvider.MergeAll(spans, Guid.Empty, S(1380), config);

        Assert.Collection(
            segments,
            intro => Assert.Equal((MediaSegmentType.Intro, S(62), S(90)), (intro.Type, intro.StartTicks, intro.EndTicks)),
            credits => Assert.Equal((MediaSegmentType.Outro, S(1300), S(1380)), (credits.Type, credits.StartTicks, credits.EndTicks)));
    }

    [Fact]
    public void ASegmentSlightlyPastTheRunTimeIsCut()
    {
        var spans = new[] { new SourceSpan(SegmentSource.IntroDb, MediaSegmentType.Outro, S(3431), S(3490)) };

        var credits = Assert.Single(StitchSegmentProvider.MergeAll(spans, Guid.Empty, S(3487), new PluginConfiguration()));

        Assert.Equal(S(3487), credits.EndTicks);
    }

    [Fact]
    public void ASegmentFarPastTheRunTimeIsDropped()
    {
        // Another release's or another episode's credits.
        var spans = new[] { new SourceSpan(SegmentSource.IntroDb, MediaSegmentType.Outro, S(3431), S(3500)) };

        Assert.Empty(StitchSegmentProvider.MergeAll(spans, Guid.Empty, S(3400), new PluginConfiguration()));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("Aired", null, false)]
    [InlineData("alt-dvd", "dvd", false)]
    [InlineData("absolute", "absolute", false)]
    [InlineData("storyArc", null, true)]
    public void DisplayOrders(string? order, string? mapping, bool unsupported)
    {
        Assert.Equal(mapping, MediaIdentity.GetDisplayOrder(new Series { DisplayOrder = order! }, out var isUnsupported));
        Assert.Equal(unsupported, isUnsupported);
    }

    [Fact]
    public void AMovieIsFetchedWhenAdded()
        => Assert.True(OnDemandFetchService.ShouldFetchOnItemAdded(new Movie(), null));

    [Fact]
    public void AnEpisodeWaitsForItsSeriesIds()
    {
        var series = new Series();
        Assert.False(OnDemandFetchService.ShouldFetchOnItemAdded(new Episode(), null));
        Assert.False(OnDemandFetchService.ShouldFetchOnItemAdded(new Episode(), series));

        series.SetProviderId("Tvdb", "81189");
        Assert.True(OnDemandFetchService.ShouldFetchOnItemAdded(new Episode(), series));
    }

    private static MediaIdentity Episode(int season, int episode, long? tvdbEpisodeId, string? mapping = null)
        => new(false, 81797, null, null, "tt0388629", season, episode, mapping, false, tvdbEpisodeId);

    private static readonly Dictionary<long, (int Season, int Episode)> OnePiece = new() { [361897] = (2, 1), [999] = (0, 1) };

    [Fact]
    public void IntroDbIsAskedInImdbNumbersFoundByTvdbId()
        => Assert.Equal((2, 1), StitchSegmentProvider.IntroDbNumbers(Episode(1, 9, 361897), OnePiece));

    [Fact]
    public void IntroDbIsAskedInImdbNumbersForAnyDisplayOrder()
        => Assert.Equal((2, 1), StitchSegmentProvider.IntroDbNumbers(Episode(1, 9, 361897, "absolute"), OnePiece));

    [Fact]
    public void IntroDbIsNotAskedForAnImdbSpecial()
        => Assert.Null(StitchSegmentProvider.IntroDbNumbers(Episode(1, 3, 999), OnePiece));

    [Fact]
    public void WithoutAMatchIntroDbIsAskedInTheEpisodesOwnDefaultOrderNumbers()
    {
        Assert.Equal((1, 9), StitchSegmentProvider.IntroDbNumbers(Episode(1, 9, null), OnePiece));
        Assert.Equal((1, 9), StitchSegmentProvider.IntroDbNumbers(Episode(1, 9, 12345), OnePiece));
        Assert.Null(StitchSegmentProvider.IntroDbNumbers(Episode(1, 9, null, "dvd"), OnePiece));
        Assert.Null(StitchSegmentProvider.IntroDbNumbers(Episode(0, 1, null), OnePiece));
    }

    [Theory]
    [InlineData(MediaSegmentType.Outro, 0, true)] // credits "from 0"
    [InlineData(MediaSegmentType.Outro, 46.881, true)] // the opening song filed as credits
    [InlineData(MediaSegmentType.Outro, 1382, false)]
    [InlineData(MediaSegmentType.Preview, 1459, false)]
    [InlineData(MediaSegmentType.Intro, 258, false)]
    [InlineData(MediaSegmentType.Intro, 900, true)]
    [InlineData(MediaSegmentType.Recap, 0, false)]
    public void SegmentsOutsideTheirHalfAreDropped(MediaSegmentType type, double start, bool dropped)
        => Assert.Equal(!dropped, StitchSegmentProvider.IsPlausible(new SourceSpan(SegmentSource.TheIntroDb, type, S(start), S(start + 30)), S(1440)));

    [Fact]
    public void WithoutARunTimeEverySegmentIsPlausible()
        => Assert.True(StitchSegmentProvider.IsPlausible(new SourceSpan(SegmentSource.TheIntroDb, MediaSegmentType.Outro, 0, S(30)), null));

    [Fact]
    public void OnePieceEpisodeOneGetsIntroDbsCredits()
    {
        // TheIntroDB files the opening song as credits; IntroDB has the real ones.
        var spans = new[]
        {
            new SourceSpan(SegmentSource.TheIntroDb, MediaSegmentType.Outro, S(46.881), S(124.5)),
            new SourceSpan(SegmentSource.TheIntroDb, MediaSegmentType.Preview, S(1459), S(1500)),
            new SourceSpan(SegmentSource.IntroDb, MediaSegmentType.Intro, S(28.783), S(118.783)),
            new SourceSpan(SegmentSource.IntroDb, MediaSegmentType.Outro, S(1382), S(1422)),
        };

        var segments = StitchSegmentProvider.MergeAll(spans, Guid.Empty, S(1500), new PluginConfiguration());

        Assert.Equal(
            [(MediaSegmentType.Intro, S(28.783), S(118.783)), (MediaSegmentType.Outro, S(1382), S(1422)), (MediaSegmentType.Preview, S(1459), S(1500))],
            segments.Select(x => (x.Type, x.StartTicks, x.EndTicks)));
    }

    [Fact]
    public void CreditsFromZeroAreDropped()
    {
        // One Piece S9E10: TheIntroDB credits start 0 with an open end, which ran to the runtime.
        var spans = new[]
        {
            new SourceSpan(SegmentSource.TheIntroDb, MediaSegmentType.Outro, 0, S(1440)),
            new SourceSpan(SegmentSource.TheIntroDb, MediaSegmentType.Intro, S(258), S(368)),
        };

        var intro = Assert.Single(StitchSegmentProvider.MergeAll(spans, Guid.Empty, S(1440), new PluginConfiguration()));

        Assert.Equal(MediaSegmentType.Intro, intro.Type);
    }

    [Fact]
    public void AGelatoEpisodeAsksIntroDbByItsStremioNumbers()
        => Assert.Equal((9, 3), StitchSegmentProvider.IntroDbNumbers(Episode(9, 3, 361897) with { StremioNumbers = (9, 3) }, OnePiece));

    [Theory]
    [InlineData("tt0388629:9:3", 9, 3)]
    [InlineData("tt0388629:0:12", 0, 12)]
    public void StremioIdsGiveTheirNumbers(string id, int season, int episode)
        => Assert.Equal((season, episode), MediaIdentity.ParseStremioNumbers(id));

    [Theory]
    [InlineData("tt0388629")]
    [InlineData("kitsu:1:2")]
    [InlineData(null)]
    public void OtherIdsGiveNoNumbers(string? id)
        => Assert.Null(MediaIdentity.ParseStremioNumbers(id));

    [Fact]
    public void GelatoEpisodesAreNotLookedUpWhenAdded()
    {
        var series = new Series();
        series.SetProviderId("Tvdb", "81797");
        var episode = new Episode();
        episode.SetProviderId("Stremio", "tt0388629:9:3");

        Assert.False(OnDemandFetchService.ShouldFetchOnItemAdded(episode, series));
    }

    [Fact]
    public void StreamsAndLocalFilesHaveTheirOwnPriority()
    {
        // One Piece S9E8: TheIntroDB's intro is from another release, IntroDB's fits the streams.
        var spans = new[]
        {
            new SourceSpan(SegmentSource.TheIntroDb, MediaSegmentType.Intro, S(197), S(307)),
            new SourceSpan(SegmentSource.IntroDb, MediaSegmentType.Intro, S(31.931), S(110.772)),
        };
        var config = new PluginConfiguration();

        var local = Assert.Single(StitchSegmentProvider.MergeAll(spans, Guid.Empty, S(1440), config));
        var stream = Assert.Single(StitchSegmentProvider.MergeAll(spans, Guid.Empty, S(1440), config, isStream: true));

        Assert.Equal(S(197), local.StartTicks);
        Assert.Equal(S(31.931), stream.StartTicks);
    }
}
