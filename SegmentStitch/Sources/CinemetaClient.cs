using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SegmentStitch.Sources;

/// <summary>
/// Reads a series' episodes from Cinemeta, Stremio's IMDb-based metadata, which lists each
/// episode in IMDb's numbering together with its TVDB episode id. IntroDB is asked in IMDb's
/// numbering, so this translates a Jellyfin episode into IntroDB's season and episode.
/// </summary>
public class CinemetaClient
{
    internal const string BaseUrl = "https://v3-cinemeta.strem.io/meta/series/";

    /// <summary>
    /// How long a series' episode list is kept; new episodes appear within a day.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(1);

    // One request per series, shared by the episodes of a scan that ask at once.
    private static readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, Lazy<Task<Dictionary<long, (int Season, int Episode)>?>> Episodes)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CinemetaClient"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client for requests.</param>
    /// <param name="logger">Logger instance.</param>
    public CinemetaClient(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// The series' IMDb season and episode by TVDB episode id.
    /// </summary>
    /// <param name="seriesImdbId">IMDb id of the series.</param>
    /// <returns>The episodes, empty when Cinemeta does not know the series, null when it could not be asked.</returns>
    public Task<Dictionary<long, (int Season, int Episode)>?> GetEpisodesAsync(string seriesImdbId)
    {
        var now = DateTime.UtcNow;
        var entry = Cache.AddOrUpdate(
            seriesImdbId,
            _ => (now + Ttl, new(() => FetchAsync(seriesImdbId))),
            (_, old) => old.ExpiresUtc > now ? old : (now + Ttl, new(() => FetchAsync(seriesImdbId))));
        var task = entry.Episodes.Value;

        // A failure is not kept: the next episode asks again.
        task.ContinueWith(
            t =>
            {
                if (t.Result is null)
                {
                    Cache.TryRemove(new KeyValuePair<string, (DateTime, Lazy<Task<Dictionary<long, (int, int)>?>>)>(seriesImdbId, entry));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// Reads Cinemeta's answer.
    /// </summary>
    /// <param name="json">The answer of GET /meta/series/{imdbId}.json.</param>
    /// <returns>IMDb season and episode by TVDB episode id.</returns>
    internal static Dictionary<long, (int Season, int Episode)> Parse(string json)
    {
        var episodes = new Dictionary<long, (int, int)>();
        foreach (var video in JsonSerializer.Deserialize<CinemetaResponse>(json)?.Meta?.Videos ?? [])
        {
            if (video.TvdbId is > 0 && video.Season is >= 0 && video.Episode is > 0)
            {
                episodes.TryAdd(video.TvdbId.Value, (video.Season.Value, video.Episode.Value));
            }
        }

        return episodes;
    }

    private async Task<Dictionary<long, (int Season, int Episode)>?> FetchAsync(string seriesImdbId)
    {
        var requestUri = new Uri(BaseUrl + Uri.EscapeDataString(seriesImdbId) + ".json", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Cinemeta answered {StatusCode} for {Uri}", (int)response.StatusCode, requestUri);
                return null;
            }

            return Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cinemeta request failed for {Uri}", requestUri);
            return null;
        }
    }

    private sealed class CinemetaResponse
    {
        [JsonPropertyName("meta")]
        public CinemetaMeta? Meta { get; set; }
    }

    private sealed class CinemetaMeta
    {
        [JsonPropertyName("videos")]
        public List<CinemetaVideo>? Videos { get; set; }
    }

    private sealed class CinemetaVideo
    {
        [JsonPropertyName("season")]
        public int? Season { get; set; }

        [JsonPropertyName("episode")]
        public int? Episode { get; set; }

        [JsonPropertyName("tvdb_id")]
        public long? TvdbId { get; set; }
    }
}
