using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SegmentStitch.Sources;

/// <summary>
/// HTTP client for TheIntroDB's GET /media (v4), taken from TheIntroDB's Jellyfin plugin.
/// Rate limit: 30 requests per 10 seconds anonymous, 120 authenticated, plus a daily usage
/// budget. Requests are paced below the window, and a 429 for the daily budget parks all
/// requests until the bucket resets.
/// </summary>
public class TheIntroDbClient
{
    // Pacing sits below the API's ceiling: pacing exactly at it draws 429s under jitter.
    private const int AnonymousRequestsPerWindow = 25;
    private const int AuthenticatedRequestsPerWindow = 100;
    private const int RateResetClampSeconds = 5 * 60;
    private const int UsageResetClampSeconds = 24 * 60 * 60;
    private const int DefaultRetryAfterSeconds = 5 * 60;
    private const int MaxConsecutiveRateLimitMultiplier = 8;

    internal const string BaseUrl = "https://api.theintrodb.org/v4";

    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinDelayBetweenRequests = TimeSpan.FromMilliseconds(RateLimitWindow.TotalMilliseconds / AnonymousRequestsPerWindow);
    private static readonly TimeSpan MinDelayBetweenRequestsAuthenticated = TimeSpan.FromMilliseconds(RateLimitWindow.TotalMilliseconds / AuthenticatedRequestsPerWindow);

    private static readonly SemaphoreSlim RateLimitLock = new(1, 1);
    private static readonly object ExpiryLock = new();
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static DateTime _nextAllowedSendUtc = DateTime.MinValue;
    private static DateTime _rateLimitExpiryUtc = DateTime.MinValue;
    private static DateTime _lastSkipLoggedExpiryUtc = DateTime.MinValue;
    private static int _consecutiveRateLimits;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TheIntroDbClient"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client for requests.</param>
    /// <param name="logger">Logger instance.</param>
    public TheIntroDbClient(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    private static DateTime RateLimitExpiryUtc
    {
        get
        {
            lock (ExpiryLock)
            {
                return _rateLimitExpiryUtc;
            }
        }

        set
        {
            lock (ExpiryLock)
            {
                _rateLimitExpiryUtc = value;
            }
        }
    }

    /// <summary>
    /// Fetches the segments of a movie or episode.
    /// </summary>
    /// <param name="query">The media to ask for.</param>
    /// <param name="apiKey">Optional API key; the caller's submissions are weighted higher with it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status, and the response when found.</returns>
    public async Task<(LookupStatus Status, TheIntroDbMediaResponse? Response)> GetMediaAsync(
        TheIntroDbQuery query,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (IsRateLimitActive())
        {
            return (LookupStatus.Failed, null);
        }

        var requestUri = new Uri(BaseUrl + "/media" + BuildMediaQuery(query), UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
        if (hasApiKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey!.Trim());
        }

        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var version = typeof(TheIntroDbClient).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("segment-stitch-jellyfin-plugin", version));

        try
        {
            await WaitForRateLimitAsync(hasApiKey ? MinDelayBetweenRequestsAuthenticated : MinDelayBetweenRequests, cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("TheIntroDB answered {StatusCode} for {Uri}", response.StatusCode, requestUri);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var retryAfterSeconds = GetRetryAfterSeconds(response.Headers, body);
                var consecutive = Interlocked.Increment(ref _consecutiveRateLimits);
                var isUsageLimit = IsUsageLimitResponse(body) || retryAfterSeconds > RateResetClampSeconds;
                var waitSeconds = isUsageLimit
                    ? retryAfterSeconds
                    : ApplyConsecutiveBackOff(retryAfterSeconds, consecutive);

                RateLimitExpiryUtc = DateTime.UtcNow.AddSeconds(waitSeconds);
                _logger.LogWarning(
                    "TheIntroDB {LimitKind} exceeded, no requests until {RateLimitExpiryUtc} UTC",
                    isUsageLimit ? "daily usage limit" : "rate limit",
                    RateLimitExpiryUtc);
                return (LookupStatus.Failed, null);
            }

            Interlocked.Exchange(ref _consecutiveRateLimits, 0);
            UpdateRateWindowFromHeaders(response.Headers);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (LookupStatus.None, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TheIntroDB answered {StatusCode} for {Uri}", (int)response.StatusCode, requestUri);
                return (LookupStatus.Failed, null);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<TheIntroDbMediaResponse>(json);

            // A 200 without a usable body is not an empty dataset.
            return result is null ? (LookupStatus.Failed, null) : (LookupStatus.Found, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "TheIntroDB request failed for {Uri}", requestUri);
            return (LookupStatus.Failed, null);
        }
    }

    /// <summary>
    /// Builds the query string for GET /media. The media is addressed by one id, in the API's
    /// order (tvdb_id, tmdb_id, imdb_id): only a TVDB-addressed request carries the caller's
    /// episode order and episode id.
    /// </summary>
    /// <param name="query">The media to ask for.</param>
    /// <returns>The query string, starting with '?'.</returns>
    internal static string BuildMediaQuery(TheIntroDbQuery query)
    {
        var hasTvdb = query.TvdbId is > 0;
        var queryParams = new List<string>(6);
        if (hasTvdb)
        {
            queryParams.Add(FormattableString.Invariant($"tvdb_id={query.TvdbId}"));
        }
        else if (query.TmdbId is > 0)
        {
            queryParams.Add(FormattableString.Invariant($"tmdb_id={query.TmdbId}"));
        }
        else
        {
            queryParams.Add($"imdb_id={Uri.EscapeDataString(query.ImdbId ?? string.Empty)}");
        }

        if (!query.IsMovie)
        {
            queryParams.Add(FormattableString.Invariant($"season={query.Season}"));
            queryParams.Add(FormattableString.Invariant($"episode={query.Episode}"));

            if (hasTvdb && !string.IsNullOrWhiteSpace(query.Mapping))
            {
                queryParams.Add($"mapping={Uri.EscapeDataString(query.Mapping.Trim())}");
            }

            if (hasTvdb && query.TvdbEpisodeId is > 0)
            {
                queryParams.Add(FormattableString.Invariant($"tvdb_episode_id={query.TvdbEpisodeId.Value}"));
            }
        }

        if (query.DurationMs is > 0)
        {
            queryParams.Add(FormattableString.Invariant($"duration_ms={query.DurationMs.Value}"));
        }

        return "?" + string.Join('&', queryParams);
    }

    /// <summary>
    /// Computes how long to wait after a 429. A usage-limit reset (seconds until UTC midnight)
    /// is trusted for up to 24 hours, a rate-limit reset for up to five minutes.
    /// </summary>
    /// <param name="headers">Response headers.</param>
    /// <param name="body">Response body.</param>
    /// <returns>Seconds to wait before the next request.</returns>
    internal static int GetRetryAfterSeconds(HttpResponseHeaders headers, string? body)
    {
        var maxClamp = IsUsageLimitResponse(body) ? UsageResetClampSeconds : RateResetClampSeconds;

        if (headers.TryGetValues("X-UsageLimit-Reset", out var usageResetValues)
            && int.TryParse(usageResetValues.FirstOrDefault(), CultureInfo.InvariantCulture, out var usageResetSeconds)
            && usageResetSeconds > 0)
        {
            // A usage reset beyond the rate-limit ceiling is unambiguous without the body.
            if (usageResetSeconds > RateResetClampSeconds)
            {
                maxClamp = UsageResetClampSeconds;
            }

            return ClampRetryAfterSeconds(usageResetSeconds, maxClamp);
        }

        if (headers.TryGetValues("X-RateLimit-Reset", out var rateResetValues)
            && int.TryParse(rateResetValues.FirstOrDefault(), CultureInfo.InvariantCulture, out var rateResetSeconds)
            && rateResetSeconds > 0)
        {
            return ClampRetryAfterSeconds(rateResetSeconds, maxClamp);
        }

        if (headers.RetryAfter?.Delta is { } delta)
        {
            return ClampRetryAfterSeconds((int)delta.TotalSeconds, maxClamp);
        }

        if (headers.RetryAfter?.Date is { } date)
        {
            return ClampRetryAfterSeconds((int)Math.Ceiling((date.UtcDateTime - DateTime.UtcNow).TotalSeconds), maxClamp);
        }

        return DefaultRetryAfterSeconds;
    }

    /// <summary>
    /// Grows the wait across consecutive 429s, capped at the rate-limit ceiling.
    /// </summary>
    /// <param name="baseSeconds">Base wait from the response headers.</param>
    /// <param name="consecutive">Number of consecutive 429s.</param>
    /// <returns>Adjusted wait in seconds.</returns>
    internal static int ApplyConsecutiveBackOff(int baseSeconds, int consecutive)
    {
        if (consecutive <= 1)
        {
            return baseSeconds;
        }

        var multiplier = Math.Min(consecutive, MaxConsecutiveRateLimitMultiplier);
        return Math.Min(baseSeconds * multiplier, RateResetClampSeconds);
    }

    private static int ClampRetryAfterSeconds(int seconds, int maxClamp)
        => Math.Max(1, Math.Min(seconds, maxClamp));

    private static bool IsUsageLimitResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var codeElement)
                && codeElement.ValueKind == JsonValueKind.String
                && codeElement.GetString() is "usage_limit_exceeded" or "specific_media_usage_limit_exceeded";
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's plain-text 429): the headers decide.
            return false;
        }
    }

    /// <summary>
    /// When the API says almost no rate-limit budget is left, holds the next request until the
    /// window resets, in case another consumer shares the key or public IP.
    /// </summary>
    private static void UpdateRateWindowFromHeaders(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            || !int.TryParse(remainingValues.FirstOrDefault(), CultureInfo.InvariantCulture, out var remaining)
            || remaining > 1)
        {
            return;
        }

        if (!headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            || !int.TryParse(resetValues.FirstOrDefault(), CultureInfo.InvariantCulture, out var resetSeconds)
            || resetSeconds <= 0)
        {
            return;
        }

        var resetUtc = DateTime.UtcNow.AddSeconds(ClampRetryAfterSeconds(resetSeconds, RateResetClampSeconds));
        if (resetUtc > _nextAllowedSendUtc)
        {
            _nextAllowedSendUtc = resetUtc;
        }
    }

    private static async Task WaitForRateLimitAsync(TimeSpan minimumDelay, CancellationToken cancellationToken)
    {
        await RateLimitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            var waitUntil = now;
            if (_lastRequestUtc != DateTime.MinValue && _lastRequestUtc + minimumDelay > waitUntil)
            {
                waitUntil = _lastRequestUtc + minimumDelay;
            }

            if (_nextAllowedSendUtc > waitUntil)
            {
                waitUntil = _nextAllowedSendUtc;
            }

            if (waitUntil > now)
            {
                await Task.Delay(waitUntil - now, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            RateLimitLock.Release();
        }
    }

    /// <summary>
    /// Whether a 429 back-off is running. Warns once per back-off instead of once per item.
    /// </summary>
    private bool IsRateLimitActive()
    {
        var expiryUtc = RateLimitExpiryUtc;
        if (DateTime.UtcNow >= expiryUtc)
        {
            return false;
        }

        if (expiryUtc != _lastSkipLoggedExpiryUtc)
        {
            _lastSkipLoggedExpiryUtc = expiryUtc;
            _logger.LogWarning("TheIntroDB rate limit active, skipping requests until {RateLimitExpiryUtc} UTC", expiryUtc);
        }

        return true;
    }
}
