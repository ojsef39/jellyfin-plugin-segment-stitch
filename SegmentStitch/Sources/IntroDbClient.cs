using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.Extensions.Logging;
using SegmentStitch.Merging;

namespace SegmentStitch.Sources;

/// <summary>
/// HTTP client for IntroDB's GET /segments, after Gelato's. IntroDB only takes a series' or
/// movie's IMDb id, with IMDb's season and episode numbers.
/// </summary>
public class IntroDbClient
{
    internal const string BaseUrl = "https://api.introdb.app/segments";

    // IntroDB publishes no rate limit; stay gentle and back off on a 429.
    private static readonly TimeSpan MinDelayBetweenRequests = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RateLimitBackOff = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim RateLimitLock = new(1, 1);
    private static readonly object ExpiryLock = new();
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static DateTime _rateLimitExpiryUtc = DateTime.MinValue;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbClient"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client for requests.</param>
    /// <param name="logger">Logger instance.</param>
    public IntroDbClient(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the intro, recap and credits of an episode, or the credits of a movie.
    /// </summary>
    /// <param name="imdbId">IMDb id of the series or movie.</param>
    /// <param name="season">IMDb season number; null for a movie.</param>
    /// <param name="episode">IMDb episode number; null for a movie.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lookup.</returns>
    public async Task<SourceLookup> GetSegmentsAsync(string imdbId, int? season, int? episode, CancellationToken cancellationToken)
    {
        lock (ExpiryLock)
        {
            if (DateTime.UtcNow < _rateLimitExpiryUtc)
            {
                return SourceLookup.Failed();
            }
        }

        var query = season is null || episode is null
            ? $"imdb_id={Uri.EscapeDataString(imdbId)}&is_movie=true"
            : FormattableString.Invariant($"imdb_id={Uri.EscapeDataString(imdbId)}&season={season}&episode={episode}");
        var requestUri = new Uri(BaseUrl + "?" + query, UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var version = typeof(IntroDbClient).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("segment-stitch-jellyfin-plugin", version));

        try
        {
            await WaitForTurnAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("IntroDB answered {StatusCode} for {Uri}", response.StatusCode, requestUri);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                lock (ExpiryLock)
                {
                    _rateLimitExpiryUtc = DateTime.UtcNow + RateLimitBackOff;
                }

                _logger.LogWarning("IntroDB rate limit hit, no requests for {BackOff}", RateLimitBackOff);
                return SourceLookup.Failed();
            }

            // 400: numbers IntroDB does not take, asking again does not change that.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return SourceLookup.None();
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("IntroDB answered {StatusCode} for {Uri}", (int)response.StatusCode, requestUri);
                return SourceLookup.Failed();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<IntroDbResponse>(json);
            return payload is null ? SourceLookup.Failed() : SourceLookup.Found(ToSpans(payload));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "IntroDB request failed for {Uri}", requestUri);
            return SourceLookup.Failed();
        }
    }

    /// <summary>
    /// The answer's segments. A post-credits scene is content: it gets no segment, and the
    /// credits stop at it.
    /// </summary>
    /// <param name="payload">IntroDB's answer.</param>
    /// <returns>The segments, empty when IntroDB has none.</returns>
    internal static List<SourceSpan> ToSpans(IntroDbResponse payload)
    {
        var spans = new List<SourceSpan>();
        Add(payload.Intro, MediaSegmentType.Intro);
        Add(payload.Recap, MediaSegmentType.Recap);
        if (payload.Outro is { } outro && OutsideScene(outro, payload.PostCredits) is { } credits)
        {
            Add(credits, MediaSegmentType.Outro);
        }

        return spans;

        void Add(IntroDbTiming? timing, MediaSegmentType type)
        {
            if (timing is not null && timing.StartMs >= 0 && timing.EndMs > timing.StartMs)
            {
                spans.Add(new SourceSpan(
                    SegmentSource.IntroDb,
                    type,
                    timing.StartMs * TimeSpan.TicksPerMillisecond,
                    timing.EndMs * TimeSpan.TicksPerMillisecond));
            }
        }
    }

    /// <summary>
    /// The credits without the post-credits scene they overlap: they end where a scene in them
    /// starts, or start where a scene at their start ends. Null when no credits are left.
    /// </summary>
    private static IntroDbTiming? OutsideScene(IntroDbTiming outro, IntroDbTiming? scene)
    {
        if (scene is null || scene.EndMs <= outro.StartMs || scene.StartMs >= outro.EndMs)
        {
            return outro;
        }

        var cut = scene.StartMs > outro.StartMs
            ? new IntroDbTiming { StartMs = outro.StartMs, EndMs = scene.StartMs }
            : new IntroDbTiming { StartMs = scene.EndMs, EndMs = outro.EndMs };
        return cut.EndMs > cut.StartMs ? cut : null;
    }

    private static async Task WaitForTurnAsync(CancellationToken cancellationToken)
    {
        await RateLimitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _lastRequestUtc + MinDelayBetweenRequests - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            RateLimitLock.Release();
        }
    }

    /// <summary>
    /// IntroDB's answer; a segment it has nothing for is null.
    /// </summary>
    internal sealed class IntroDbResponse
    {
        [JsonPropertyName("intro")]
        public IntroDbTiming? Intro { get; set; }

        [JsonPropertyName("recap")]
        public IntroDbTiming? Recap { get; set; }

        [JsonPropertyName("outro")]
        public IntroDbTiming? Outro { get; set; }

        [JsonPropertyName("post_credits")]
        public IntroDbTiming? PostCredits { get; set; }
    }

    /// <summary>
    /// One segment's times in milliseconds.
    /// </summary>
    internal sealed class IntroDbTiming
    {
        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }
    }
}
