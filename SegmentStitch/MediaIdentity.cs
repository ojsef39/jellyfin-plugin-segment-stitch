using System;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace SegmentStitch;

/// <summary>
/// What the sources need to know to address an item.
/// </summary>
/// <param name="IsMovie">True for a movie.</param>
/// <param name="TvdbId">TVDB id of the movie or series.</param>
/// <param name="TmdbId">TMDB id of the movie or series.</param>
/// <param name="ImdbId">IMDb id of the movie or episode, else of the series.</param>
/// <param name="SeriesOrMovieImdbId">IMDb id of the movie or series.</param>
/// <param name="Season">Season number in the series' display order.</param>
/// <param name="Episode">Episode number in the series' display order.</param>
/// <param name="Mapping">TVDB name of the series' display order; null for the default order.</param>
/// <param name="UnsupportedOrder">True when the display order has no TVDB name.</param>
/// <param name="TvdbEpisodeId">TVDB id of the episode.</param>
/// <param name="StremioNumbers">Season and episode of a Gelato episode's Stremio id, in Cinemeta's (IMDb) numbering.</param>
internal sealed record MediaIdentity(
    bool IsMovie,
    int? TvdbId,
    int? TmdbId,
    string? ImdbId,
    string? SeriesOrMovieImdbId,
    int? Season,
    int? Episode,
    string? Mapping,
    bool UnsupportedOrder,
    long? TvdbEpisodeId,
    (int Season, int Episode)? StremioNumbers = null)
{
    /// <summary>
    /// Reads an item's identity.
    /// </summary>
    /// <param name="item">A movie, episode or other video.</param>
    /// <returns>The identity.</returns>
    public static MediaIdentity Of(BaseItem item)
    {
        if (item is Episode episode)
        {
            var mapping = GetDisplayOrder(episode.Series, out var unsupported);
            return new MediaIdentity(
                false,
                GetInt(episode.Series, "Tvdb"),
                GetInt(episode.Series, "Tmdb"),
                GetString(episode, "Imdb") ?? GetString(episode.Series, "Imdb"),
                GetString(episode.Series, "Imdb"),
                episode.ParentIndexNumber,
                episode.IndexNumber,
                mapping,
                unsupported,
                GetLong(episode, "Tvdb"),
                ParseStremioNumbers(GetString(episode, "Stremio")));
        }

        var imdbId = GetString(item, "Imdb");
        var isMovie = item is Movie;
        return new MediaIdentity(
            isMovie,
            GetInt(item, "Tvdb"),
            GetInt(item, "Tmdb"),
            imdbId,
            isMovie ? imdbId : null,
            isMovie ? null : item.ParentIndexNumber,
            isMovie ? null : item.IndexNumber,
            null,
            false,
            null);
    }

    /// <summary>
    /// Reads the series' display order as the TVDB order name to send, or null for the default
    /// (aired) order, which is what TheIntroDB stores.
    /// </summary>
    /// <remarks>
    /// <c>Series.DisplayOrder</c> holds the vocabulary of the provider that filled the series.
    /// The TVDB plugin's names (official, dvd, absolute, alternate, altdvd, alttwo, regional)
    /// are recognized. The TMDB provider's episode-group names (originalAirDate, digital,
    /// storyArc, production, tv) have no TVDB counterpart and are reported as unsupported.
    /// Taken from TheIntroDB's Jellyfin plugin.
    /// </remarks>
    /// <param name="series">The episode's series.</param>
    /// <param name="unsupported">True when the order has no TVDB name.</param>
    /// <returns>The order name, or null.</returns>
    internal static string? GetDisplayOrder(Series? series, out bool unsupported)
    {
        unsupported = false;
        var displayOrder = series?.DisplayOrder;
        if (string.IsNullOrWhiteSpace(displayOrder))
        {
            return null;
        }

        var key = displayOrder.Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        switch (key)
        {
            case "OFFICIAL":
            case "OFFICIALORDER":
            case "AIRED":
            case "AIREDORDER":
            case "AIRDATE":
            case "AIRDATEORDER":
            case "DEFAULT":
            case "DEFAULTORDER":
                return null;
            case "DVD":
            case "ALTDVD":
            case "DVDORDER":
                return "dvd";
            case "ABSOLUTE":
            case "ABSOLUTEORDER":
            case "ABSO":
                return "absolute";
            case "ALTERNATE":
            case "ALTERNATEORDER":
            case "ALTTWO":
                return "alternate";
            case "REGIONAL":
            case "REGIONALORDER":
                return "regional";
            default:
                unsupported = true;
                return null;
        }
    }

    /// <summary>
    /// The season and episode of a Stremio episode id ("tt0388629:9:3").
    /// </summary>
    /// <param name="stremioId">The id.</param>
    /// <returns>The numbers, or null for another id.</returns>
    internal static (int Season, int Episode)? ParseStremioNumbers(string? stremioId)
    {
        var parts = stremioId?.Split(':');
        return parts is { Length: 3 }
            && parts[0].StartsWith("tt", StringComparison.Ordinal)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var season)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode)
            ? (season, episode)
            : null;
    }

    private static string? GetString(BaseItem? item, string provider)
        => item?.ProviderIds is { } ids && ids.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id) ? id.Trim() : null;

    private static int? GetInt(BaseItem? item, string provider)
        => int.TryParse(GetString(item, provider), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    private static long? GetLong(BaseItem? item, string provider)
        => long.TryParse(GetString(item, provider), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
}
