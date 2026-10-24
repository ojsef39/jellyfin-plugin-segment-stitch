using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SegmentStitch.Sources;

/// <summary>
/// Response of TheIntroDB's GET /media.
/// </summary>
public class TheIntroDbMediaResponse
{
    /// <summary>
    /// Gets or sets the TVDB ID. TVDB is the media's identity in v4: the response
    /// carries this and no TMDB id, since a media with no TMDB counterpart is a
    /// normal media rather than a fake <c>tmdb_id: 0</c>.
    /// </summary>
    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; set; }

    /// <summary>
    /// Gets or sets the type: "movie" or "tv".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number (TV only).
    /// </summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }

    /// <summary>
    /// Gets or sets the episode number (TV only).
    /// </summary>
    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    /// <summary>
    /// Gets the intro segments (always present, may have null values).
    /// </summary>
    [JsonPropertyName("intro")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Collection<TheIntroDbSegmentTimestamp> Intro { get; } = new();

    /// <summary>
    /// Gets the recap segments (always present, may have null values).
    /// </summary>
    [JsonPropertyName("recap")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Collection<TheIntroDbSegmentTimestamp> Recap { get; } = new();

    /// <summary>
    /// Gets the credits segments (always present, may have null values).
    /// </summary>
    [JsonPropertyName("credits")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Collection<TheIntroDbSegmentTimestamp> Credits { get; } = new();

    /// <summary>
    /// Gets the preview segments (always present, may have null values).
    /// </summary>
    [JsonPropertyName("preview")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Collection<TheIntroDbSegmentTimestamp> Preview { get; } = new();
}
