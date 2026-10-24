using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SegmentStitch.Configuration;
using SegmentStitch.Merging;
using SegmentStitch.Sources;

namespace SegmentStitch;

/// <summary>
/// Media segment provider that asks TheIntroDB and IntroDB, reads the item's chapters and
/// merges their segments per type.
/// </summary>
public class StitchSegmentProvider : IMediaSegmentProvider
{
    /// <summary>
    /// How long a source's "nothing for this item" holds before it is asked again.
    /// </summary>
    internal static readonly TimeSpan MissTtl = TimeSpan.FromDays(14);

    /// <summary>
    /// How far a segment may run past the runtime and be cut to it: releases differ by a few
    /// seconds, a segment far beyond the runtime belongs to another release or episode.
    /// </summary>
    internal static readonly long RunTimeToleranceTicks = TimeSpan.FromSeconds(10).Ticks;

    private static readonly MediaSegmentType[] Types =
        [MediaSegmentType.Intro, MediaSegmentType.Recap, MediaSegmentType.Outro, MediaSegmentType.Preview];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterRepository _chapterRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StitchSegmentProvider> _logger;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<IReadOnlyList<MediaSegmentDto>>>> _running = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="StitchSegmentProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory for API requests.</param>
    /// <param name="libraryManager">Library manager to resolve items.</param>
    /// <param name="chapterRepository">The items' chapters.</param>
    /// <param name="serviceProvider">Resolves IMediaSegmentManager lazily, which depends on the providers.</param>
    /// <param name="logger">Logger instance.</param>
    public StitchSegmentProvider(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        IChapterRepository chapterRepository,
        IServiceProvider serviceProvider,
        ILogger<StitchSegmentProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _chapterRepository = chapterRepository;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Segment Stitch";

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie or Video);

    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Gelato's run before a stream's playback and the playback hook can ask for one item at
    /// once: one lookup answers both.
    /// </remarks>
    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = _running.GetOrAdd(request.ItemId, _ => new Lazy<Task<IReadOnlyList<MediaSegmentDto>>>(() => LookupAsync(request, cancellationToken)));
        run.Value.ContinueWith(_ => _running.TryRemove(new KeyValuePair<Guid, Lazy<Task<IReadOnlyList<MediaSegmentDto>>>>(request.ItemId, run)), TaskScheduler.Default);
        return run.Value;
    }

    private async Task<IReadOnlyList<MediaSegmentDto>> LookupAsync(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        List<MediaSegmentDto> existing = request.ExistingSegments?.ToList() ?? [];
        if (Plugin.Instance is not { } plugin || _libraryManager.GetItemById(request.ItemId) is not { } item)
        {
            return existing;
        }

        var config = plugin.Configuration;
        var refreshKey = string.Create(CultureInfo.InvariantCulture, $"item:{item.Id:N}");
        var segmentManager = _serviceProvider.GetRequiredService<IMediaSegmentManager>();
        var served = (await segmentManager
            .GetSegmentsAsync(item, null, _libraryManager.GetLibraryOptions(item), filterByProvider: true)
            .ConfigureAwait(false)).Count();
        var refreshDue = config.RefreshAfterDays > 0 && !plugin.RecentFetches.TryGetHit(refreshKey);
        if (ShouldKeepExistingSegments(existing.Count, served, refreshDue))
        {
            return existing;
        }

        var identity = MediaIdentity.Of(item);
        var theIntroDb = config.EnableTheIntroDb
            ? LookupTheIntroDbAsync(plugin, config, item, identity, cancellationToken)
            : Task.FromResult(SourceLookup.None());
        var introDb = config.EnableIntroDb
            ? LookupIntroDbAsync(plugin, identity, cancellationToken)
            : Task.FromResult(SourceLookup.None());
        var chapters = config.EnableChapters
            ? SourceLookup.Found(ChapterSource.GetSpans(_chapterRepository.GetChapters(item.Id), item.RunTimeTicks))
            : SourceLookup.None();
        var lookups = new[] { await theIntroDb.ConfigureAwait(false), await introDb.ConfigureAwait(false), chapters };

        // A failed source must not drop what it gave before: keep the stored segments and ask
        // again on the next run, which the missing fetch record allows.
        var failed = lookups.Any(l => l.Status == LookupStatus.Failed);
        if (failed && existing.Count > 0)
        {
            _logger.LogInformation("Keeping the segments of {Name}: a source failed", item.Name);
            return existing;
        }

        if (!failed && config.RefreshAfterDays > 0)
        {
            plugin.RecentFetches.Remember(refreshKey, RefreshTtl(config.RefreshAfterDays, Random.Shared.NextDouble()));
        }

        var segments = MergeAll(
            lookups.SelectMany(l => l.Spans),
            item.Id,
            item.RunTimeTicks,
            config,
            OnDemandFetchService.IsGelato(item),
            (type, merged) =>
            {
                if (config.MergeMode == MergeMode.Smart && merged.LeftOut.Count > 0)
                {
                    _logger.LogInformation(
                        "Sources disagree on the {Type} of {Name} ({ItemId}): took {From}, left out {LeftOut}",
                        type,
                        item.Name,
                        item.Id,
                        string.Join('+', merged.From),
                        string.Join('+', merged.LeftOut));
                }
            });
        _logger.LogInformation(
            "{Count} segments for {Name} (TheIntroDB {TheIntroDb}, IntroDB {IntroDb}, chapters {Chapters})",
            segments.Count,
            item.Name,
            lookups[0].Status,
            lookups[1].Status,
            lookups[2].Status);
        return segments;
    }

    /// <summary>
    /// Whether an item keeps its segments instead of being fetched: when another provider's
    /// segments are served for it, or when this plugin's own are not due for a refresh.
    /// Segments of providers disabled in the library are not served and do not count.
    /// </summary>
    /// <param name="ownCount">This plugin's stored segments of the item.</param>
    /// <param name="servedCount">The item's segments the library serves, this plugin's included.</param>
    /// <param name="refreshDue">Whether this plugin's segments are due for a refresh.</param>
    /// <returns>Whether to keep the existing segments.</returns>
    internal static bool ShouldKeepExistingSegments(int ownCount, int servedCount, bool refreshDue)
        => servedCount > ownCount || (ownCount > 0 && !refreshDue);

    /// <summary>
    /// How long a fetch holds before the item is refreshed: 75 to 125 % of the setting, so the
    /// items of one scan do not all fall due in the same later scan.
    /// </summary>
    /// <param name="days">The configured refresh interval in days.</param>
    /// <param name="jitter">A value in [0, 1).</param>
    /// <returns>The time until the refresh.</returns>
    internal static TimeSpan RefreshTtl(int days, double jitter)
        => TimeSpan.FromDays(days * (0.75 + (0.5 * jitter)));

    /// <summary>
    /// The sources in the configured priority order; sources missing from the setting follow
    /// in the default order.
    /// </summary>
    /// <param name="setting">Comma separated source names.</param>
    /// <returns>Every source once.</returns>
    internal static IReadOnlyList<SegmentSource> ParsePriority(string? setting)
    {
        var named = (setting ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<SegmentSource>(s, ignoreCase: true, out var source) ? source : (SegmentSource?)null)
            .OfType<SegmentSource>();
        return named.Concat(Enum.GetValues<SegmentSource>()).Distinct().ToList();
    }

    /// <summary>
    /// One segment per enabled type from the sources' segments.
    /// </summary>
    /// <param name="spans">The sources' segments.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="runTimeTicks">The item's runtime, if known.</param>
    /// <param name="config">The configuration.</param>
    /// <param name="isStream">Whether the item is one of Gelato's streams, which have their own priority.</param>
    /// <param name="onMerged">Told each merged segment and the sources it came from.</param>
    /// <returns>The merged segments.</returns>
    internal static List<MediaSegmentDto> MergeAll(
        IEnumerable<SourceSpan> spans,
        Guid itemId,
        long? runTimeTicks,
        PluginConfiguration config,
        bool isStream = false,
        Action<MediaSegmentType, MergedSegment>? onMerged = null)
    {
        var usable = spans
            .Where(s => IsPlausible(s, runTimeTicks))
            .Select(s => WithinRunTime(s, runTimeTicks))
            .OfType<SourceSpan>()
            .ToList();
        var priority = ParsePriority(isStream ? config.StreamSourcePriority : config.SourcePriority);
        var segments = new List<MediaSegmentDto>();
        foreach (var type in Types.Where(t => IsEnabled(t, config)))
        {
            if (SegmentMerger.Merge(usable, type, config.MergeMode, priority) is { } merged)
            {
                onMerged?.Invoke(type, merged);
                segments.Add(new MediaSegmentDto
                {
                    ItemId = itemId,
                    Type = type,
                    StartTicks = merged.StartTicks,
                    EndTicks = merged.EndTicks,
                });
            }
        }

        return segments;
    }

    /// <summary>
    /// Whether a segment sits where its type can be: an intro or recap starts in the first half
    /// of the runtime, credits or a preview in the second. Catches an opening song filed as
    /// credits, and credits "from 0" that would cover the whole item.
    /// </summary>
    /// <param name="span">The segment.</param>
    /// <param name="runTimeTicks">The item's runtime, if known.</param>
    /// <returns>Whether to keep it.</returns>
    internal static bool IsPlausible(SourceSpan span, long? runTimeTicks)
    {
        if (runTimeTicks is not > 0)
        {
            return true;
        }

        var inFirstHalf = span.StartTicks < runTimeTicks.Value / 2;
        return span.Type is MediaSegmentType.Intro or MediaSegmentType.Recap ? inFirstHalf : !inFirstHalf;
    }

    /// <summary>
    /// The segment cut to the runtime, or null when it runs past it by more than the tolerance.
    /// </summary>
    private static SourceSpan? WithinRunTime(SourceSpan span, long? runTimeTicks)
    {
        if (runTimeTicks is not > 0 || span.EndTicks <= runTimeTicks.Value)
        {
            return span;
        }

        return span.EndTicks - runTimeTicks.Value > RunTimeToleranceTicks || span.StartTicks >= runTimeTicks.Value
            ? null
            : span with { EndTicks = runTimeTicks.Value };
    }

    private static bool IsEnabled(MediaSegmentType type, PluginConfiguration config) => type switch
    {
        MediaSegmentType.Intro => config.EnableIntro,
        MediaSegmentType.Recap => config.EnableRecap,
        MediaSegmentType.Outro => config.EnableCredits,
        MediaSegmentType.Preview => config.EnablePreview,
        _ => false,
    };

    private static void AddTheIntroDbSpans(
        IEnumerable<TheIntroDbSegmentTimestamp> stamps,
        bool endRequired,
        MediaSegmentType type,
        long? runTimeTicks,
        List<SourceSpan> spans)
    {
        foreach (var stamp in stamps)
        {
            if (stamp is null || !stamp.HasValidRange(endRequired))
            {
                continue;
            }

            var startTicks = (stamp.StartMs ?? 0) * TimeSpan.TicksPerMillisecond;
            long? endTicks = stamp.EndMs is > 0 ? stamp.EndMs.Value * TimeSpan.TicksPerMillisecond : runTimeTicks;
            if (endTicks > startTicks)
            {
                spans.Add(new SourceSpan(SegmentSource.TheIntroDb, type, startTicks, endTicks.Value));
            }
        }
    }

    private async Task<SourceLookup> LookupTheIntroDbAsync(
        Plugin plugin,
        PluginConfiguration config,
        BaseItem item,
        MediaIdentity id,
        CancellationToken cancellationToken)
    {
        // A non-default order can only be sent with a TVDB id: a tmdb_id request is read in
        // TMDB numbering and would answer another episode. No segments beat wrong ones.
        if (!id.IsMovie && (id.UnsupportedOrder || (id.Mapping is not null && id.TvdbId is not > 0)))
        {
            return SourceLookup.None();
        }

        if ((id.TvdbId is not > 0 && id.TmdbId is not > 0 && string.IsNullOrWhiteSpace(id.ImdbId))
            || (!id.IsMovie && (id.Season is null || id.Episode is null)))
        {
            return SourceLookup.None();
        }

        var query = new TheIntroDbQuery(
            id.TvdbId,
            id.TmdbId,
            id.ImdbId,
            id.IsMovie,
            id.Season,
            id.Episode,
            id.Mapping,
            id.TvdbEpisodeId,
            item.RunTimeTicks is > 0 ? item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond : null);
        var missKey = TheIntroDbClient.BuildMediaQuery(query with { DurationMs = null });
        if (plugin.TheIntroDbMisses.TryGetHit(missKey))
        {
            return SourceLookup.None();
        }

        var client = new TheIntroDbClient(_httpClientFactory.CreateClient(), _logger);
        var (status, media) = await client.GetMediaAsync(query, config.TheIntroDbApiKey, cancellationToken).ConfigureAwait(false);
        if (status == LookupStatus.Failed)
        {
            return SourceLookup.Failed();
        }

        var spans = new List<SourceSpan>();
        if (media is not null)
        {
            AddTheIntroDbSpans(media.Intro, true, MediaSegmentType.Intro, item.RunTimeTicks, spans);
            AddTheIntroDbSpans(media.Recap, true, MediaSegmentType.Recap, item.RunTimeTicks, spans);
            AddTheIntroDbSpans(media.Credits, false, MediaSegmentType.Outro, item.RunTimeTicks, spans);
            AddTheIntroDbSpans(media.Preview, false, MediaSegmentType.Preview, item.RunTimeTicks, spans);
        }

        if (spans.Count == 0)
        {
            plugin.TheIntroDbMisses.Remember(missKey, MissTtl);
        }

        return SourceLookup.Found(spans);
    }

    /// <summary>
    /// IntroDB's season and episode for an episode. IntroDB reads them in IMDb's numbering, as
    /// Cinemeta and Stremio give it: a Gelato episode's Stremio id has them; a local episode is
    /// found in Cinemeta by its TVDB id; else the episode's own numbers, which only the default
    /// order may share with IMDb.
    /// </summary>
    /// <param name="id">The episode.</param>
    /// <param name="imdbByTvdb">The series' IMDb season and episode by TVDB episode id.</param>
    /// <returns>The numbers, or null when IntroDB cannot be asked.</returns>
    internal static (int Season, int Episode)? IntroDbNumbers(MediaIdentity id, IReadOnlyDictionary<long, (int Season, int Episode)> imdbByTvdb)
    {
        if (id.StremioNumbers is { } stremio)
        {
            return stremio.Season > 0 && stremio.Episode > 0 ? stremio : null;
        }

        if (id.TvdbEpisodeId is { } tvdbEpisodeId && imdbByTvdb.TryGetValue(tvdbEpisodeId, out var imdb))
        {
            // IntroDB takes no season 0.
            return imdb.Season > 0 ? imdb : null;
        }

        if (id.Mapping is not null || id.UnsupportedOrder || id.Season is not > 0 || id.Episode is not > 0)
        {
            return null;
        }

        return (id.Season.Value, id.Episode.Value);
    }

    private async Task<SourceLookup> LookupIntroDbAsync(Plugin plugin, MediaIdentity id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id.SeriesOrMovieImdbId))
        {
            return SourceLookup.None();
        }

        int? season = null;
        int? episode = null;
        if (!id.IsMovie)
        {
            // Only an episode with a TVDB id and no Stremio id needs Cinemeta.
            var imdbByTvdb = id.TvdbEpisodeId is null || id.StremioNumbers is not null
                ? []
                : await new CinemetaClient(_httpClientFactory.CreateClient(), _logger)
                    .GetEpisodesAsync(id.SeriesOrMovieImdbId)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

            // Without Cinemeta the numbers could differ from the next run's: ask again later.
            if (imdbByTvdb is null)
            {
                return SourceLookup.Failed();
            }

            if (IntroDbNumbers(id, imdbByTvdb) is not { } numbers)
            {
                return SourceLookup.None();
            }

            (season, episode) = numbers;
        }

        var missKey = id.IsMovie
            ? $"movie:{id.SeriesOrMovieImdbId}"
            : string.Create(CultureInfo.InvariantCulture, $"episode:{id.SeriesOrMovieImdbId}:{season}:{episode}");
        if (plugin.IntroDbMisses.TryGetHit(missKey))
        {
            return SourceLookup.None();
        }

        var client = new IntroDbClient(_httpClientFactory.CreateClient(), _logger);
        var lookup = await client
            .GetSegmentsAsync(id.SeriesOrMovieImdbId, season, episode, cancellationToken)
            .ConfigureAwait(false);
        if (lookup.Status == LookupStatus.None)
        {
            plugin.IntroDbMisses.Remember(missKey, MissTtl);
        }

        return lookup;
    }
}
