using System.Globalization;
using Microsoft.Extensions.Logging;
using Stackarr.Ai;
using Stackarr.Tvarr.Components.Shared;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// Deterministic recommendation candidates for films, series and anime. It combines TMDB trending,
/// top-rated, seasonal-anime and genre-discover rows, excludes everything already in the library,
/// and returns a real reason per pick. No model is involved, so the reasons stay true.
/// </summary>
public sealed class TvarrAssistantRecommendationSource(
    MediaCatalogService catalog,
    ILogger<TvarrAssistantRecommendationSource> logger) : IAssistantRecommendationSource
{
    static readonly Dictionary<string, string> GenreAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sci fi"] = "Science Fiction",
        ["sci-fi"] = "Science Fiction",
        ["scifi"] = "Science Fiction",
        ["animation"] = "Animation",
        ["cartoon"] = "Animation",
        ["rom-com"] = "Romance",
        ["romcom"] = "Romance"
    };

    public string Key => "tv";

    public bool IsAvailable => catalog.IsTmdbConfigured;

    public async Task<IReadOnlyList<AssistantRecommendation>> RecommendAsync(
        AssistantRecommendationQuery query, CancellationToken ct)
    {
        if (!IsAvailable)
            return [];

        int limit = Math.Clamp(query.Limit <= 0 ? 6 : query.Limit, 1, 15);
        bool anime = query.Anime == true
            || (!string.IsNullOrWhiteSpace(query.Domain)
                && query.Domain.Contains("anime", StringComparison.OrdinalIgnoreCase));
        bool movies = !anime && (string.IsNullOrWhiteSpace(query.Domain)
            || query.Domain.Contains("movie", StringComparison.OrdinalIgnoreCase)
            || query.Domain.Contains("film", StringComparison.OrdinalIgnoreCase));
        bool shows = !movies
            || string.IsNullOrWhiteSpace(query.Domain)
            || query.Domain.Contains("series", StringComparison.OrdinalIgnoreCase)
            || query.Domain.Contains("tv", StringComparison.OrdinalIgnoreCase)
            || query.Domain.Contains("show", StringComparison.OrdinalIgnoreCase);

        List<(MediaItem Item, string Reason)> candidates = [];

        if (anime)
        {
            List<MediaItem> seasonal = await SafeAsync(() => catalog.GetAnimeSeasonalAsync(null, null, 1, ct), []);
            candidates.AddRange(seasonal.Select(i => (i, "Airing this anime season")));

            List<MediaItem> trending = await SafeAsync(
                () => catalog.GetCategoryAsync(MediaSection.Anime, DiscoverCategory.Trending, 1, ct), []);
            candidates.AddRange(trending.Select(i => (i, "Trending anime")));
        }
        else
        {
            if (movies)
                await AddSectionAsync(candidates, MediaSection.Movies, query.Genre, ct);
            if (shows)
                await AddSectionAsync(candidates, MediaSection.Shows, query.Genre, ct);
        }

        List<AssistantRecommendation> result = candidates
            .Where(c => c.Item.TmdbId > 0 && !c.Item.IsInLibrary && !string.IsNullOrWhiteSpace(c.Item.Title))
            .GroupBy(c => (c.Item.TmdbId, IsSeries(c.Item)))
            .Select(g => g.First())
            .OrderByDescending(c => Score(c.Item))
            .Take(limit)
            .Select(c => new AssistantRecommendation(
                ToSummary(c.Item),
                Describe(c.Item, c.Reason),
                Score(c.Item)))
            .ToList();

        return result;
    }

    async Task AddSectionAsync(
        List<(MediaItem Item, string Reason)> candidates,
        MediaSection section,
        string? genre,
        CancellationToken ct)
    {
        List<MediaItem> trending = await SafeAsync(
            () => catalog.GetCategoryAsync(section, DiscoverCategory.Trending, 1, ct), []);
        candidates.AddRange(trending.Select(i => (i, "Trending now")));

        List<MediaItem> topRated = await SafeAsync(
            () => catalog.GetCategoryAsync(section, DiscoverCategory.TopRated, 1, ct), []);
        candidates.AddRange(topRated.Select(i => (i, "Top rated")));

        string? genreId = await ResolveGenreIdAsync(section, genre, ct);
        if (genreId is not null)
        {
            List<MediaItem> byGenre = await SafeAsync(
                () => catalog.DiscoverAsync(section, genreId, "vote_average.desc", null, 1, ct), []);
            candidates.AddRange(byGenre.Select(i => (i, "Highly rated in that genre")));
        }
    }

    static double Score(MediaItem item) =>
        (item.Rating ?? 0) * 10
        + Math.Min(item.Popularity, 500) / 100
        + (item.VoteCount > 1000 ? 2 : 0);

    static string Describe(MediaItem item, string reason)
    {
        string rating = item.Rating is > 0
            ? $"rated {item.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)}"
            : "";
        string year = item.Year > 0 ? item.Year.ToString(CultureInfo.InvariantCulture) : "";
        List<string> parts = [reason];
        if (rating.Length > 0) parts.Add(rating);
        if (year.Length > 0) parts.Add(year);
        return string.Join(", ", parts);
    }

    async Task<string?> ResolveGenreIdAsync(MediaSection section, string? genre, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return null;

        string value = genre.Trim();
        string normalised = GenreAliases.GetValueOrDefault(value, value);
        List<TmdbGenre> genres = await SafeAsync(() => catalog.GetGenresAsync(section, ct), []);
        TmdbGenre? match = genres.FirstOrDefault(g =>
            string.Equals(g.Name, normalised, StringComparison.OrdinalIgnoreCase));
        return match?.Id.ToString(CultureInfo.InvariantCulture);
    }

    static AssistantMediaSummary ToSummary(MediaItem item)
    {
        bool isSeries = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
        return new AssistantMediaSummary(
            TvarrAssistantMediaSource.SourceKey,
            isSeries
                ? $"{TvarrAssistantMediaSource.SourceKey}:series:{item.TmdbId}"
                : $"{TvarrAssistantMediaSource.SourceKey}:movie:{item.TmdbId}",
            isSeries ? AssistantMediaKind.Series : AssistantMediaKind.Movie,
            item.Title,
            isSeries ? "Series" : "Movie",
            item.Year > 0 ? item.Year : null,
            "Not in library",
            string.IsNullOrWhiteSpace(item.PosterUrl) ? null : item.PosterUrl);
    }

    static bool IsSeries(MediaItem item) =>
        string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

    async Task<T> SafeAsync<T>(Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Assistant recommendation lookup failed");
            return fallback;
        }
    }
}
