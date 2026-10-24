namespace SegmentStitch.Sources;

/// <summary>
/// The media a TheIntroDB request asks for.
/// </summary>
/// <param name="TvdbId">TVDB id of the movie or series; TheIntroDB's identity, so preferred.</param>
/// <param name="TmdbId">TMDB id of the movie or series, used without a TVDB id.</param>
/// <param name="ImdbId">IMDb id, used without a TVDB or TMDB id.</param>
/// <param name="IsMovie">True for a movie.</param>
/// <param name="Season">Season number, in <paramref name="Mapping"/>'s order.</param>
/// <param name="Episode">Episode number, in <paramref name="Mapping"/>'s order.</param>
/// <param name="Mapping">TVDB episode order of the numbers; null for the default order.</param>
/// <param name="TvdbEpisodeId">TVDB episode id, which pins one exact episode.</param>
/// <param name="DurationMs">The file's duration, which picks the matching release.</param>
public sealed record TheIntroDbQuery(
    int? TvdbId,
    int? TmdbId,
    string? ImdbId,
    bool IsMovie,
    int? Season,
    int? Episode,
    string? Mapping,
    long? TvdbEpisodeId,
    long? DurationMs);
