using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Mal.Clients;
using Stackarr.Mal.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.Mal.Services;


/// <summary>TMDB-first presentation data for the MAL detail popup.</summary>
public sealed record MalRelatedEntry(
    int Id,
    string Title,
    string MediaType,
    int NumEpisodes,
    string PictureUrl,
    string RelationType,
    string RelationTypeFormatted);

public sealed record MalPresentation(
    string Title,
    string Overview,
    string PosterUrl,
    string BackdropUrl,
    int Year,
    double? Rating,
    int? RuntimeMinutes,
    string Status,
    string Network,
    int TmdbId,
    bool IsMovie);

/// <summary>
/// Caches MAL metadata and enriches it with TMDB artwork/metadata. TMDB is the preferred source
/// (the app already uses it everywhere); MAL remains the source for list semantics and fallback
/// metadata when no TMDB mapping exists.
/// </summary>
public sealed class MalCatalogService(
    IDbContextFactory<StackarrDbContext> dbFactory,
    MalMappingDataset dataset,
    TmdbService tmdb,
    TimeProvider time,
    ILogger<MalCatalogService> logger)
{
    public async Task<MalCatalogEntry?> GetAsync(int malId, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<MalCatalogEntry>().AsNoTracking().FirstOrDefaultAsync(x => x.MalId == malId, ct);
    }

    readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<MalCatalogEntry> UpsertAsync(MalAnime anime, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            return await UpsertCoreAsync(anime, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    async Task<MalCatalogEntry> UpsertCoreAsync(MalAnime anime, CancellationToken ct)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MalCatalogEntry entry = await db.Set<MalCatalogEntry>().FirstOrDefaultAsync(x => x.MalId == anime.Id, ct)
            ?? new MalCatalogEntry { MalId = anime.Id };
        if (entry.FetchedAt == default)
            db.Set<MalCatalogEntry>().Add(entry);

        Apply(entry, anime, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// Bulk variant used by the list pull. One transaction for the whole page instead of one
    /// context/save per entry, which is the difference between a fast page and a stalled circuit
    /// on large lists.
    /// </summary>
    public async Task UpsertManyAsync(IReadOnlyList<MalAnime> animes, CancellationToken ct = default)
    {
        if (animes.Count == 0)
            return;

        await _writeGate.WaitAsync(ct);
        try
        {
            await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
            List<int> ids = animes.Select(x => x.Id).Distinct().ToList();
            Dictionary<int, MalCatalogEntry> existing = await db.Set<MalCatalogEntry>()
                .Where(x => ids.Contains(x.MalId))
                .ToDictionaryAsync(x => x.MalId, ct);

            DateTime now = time.GetUtcNow().UtcDateTime;
            foreach (MalAnime anime in animes)
            {
                if (!existing.TryGetValue(anime.Id, out MalCatalogEntry? entry))
                {
                    entry = new MalCatalogEntry { MalId = anime.Id };
                    db.Set<MalCatalogEntry>().Add(entry);
                    existing[anime.Id] = entry;
                }

                Apply(entry, anime, now);
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    static void Apply(MalCatalogEntry entry, MalAnime anime, DateTime now)
    {
        entry.Title = anime.Title;
        entry.EnglishTitle = anime.AlternativeTitles?.English ?? "";
        entry.JapaneseTitle = anime.AlternativeTitles?.Japanese ?? "";
        entry.Synonyms = string.Join(" | ", anime.AlternativeTitles?.Synonyms ?? []);
        string relationsJson = JsonSerializer.Serialize(
            anime.RelatedAnime.Select(x => new MalRelatedEntry(
                x.Node.Id,
                x.Node.Title,
                x.Node.MediaType,
                x.Node.NumEpisodes,
                x.Node.MainPicture?.Large ?? x.Node.MainPicture?.Medium ?? "",
                x.RelationType,
                x.RelationTypeFormatted)));
        // The list endpoint does not return related_anime; never overwrite relations learned from
        // the detail endpoint with an empty list.
        if (relationsJson != "[]" || string.IsNullOrWhiteSpace(entry.RelatedAnimeJson))
            entry.RelatedAnimeJson = relationsJson;
        entry.MainPictureUrl = anime.MainPicture?.Large is { Length: > 0 } large
            ? large
            : anime.MainPicture?.Medium ?? "";
        entry.MediaType = anime.MediaType;
        entry.NumEpisodes = anime.NumEpisodes;
        entry.AiringStatus = anime.Status;
        entry.StartDate = anime.StartDate;
        entry.EndDate = anime.EndDate;
        entry.Synopsis = anime.Synopsis;
        entry.Genres = string.Join(", ", anime.Genres.Select(x => x.Name));
        entry.Studios = string.Join(", ", anime.Studios.Select(x => x.Name));
        entry.Mean = anime.Mean;
        entry.Rank = anime.Rank;
        entry.Nsfw = !string.IsNullOrWhiteSpace(anime.Nsfw) && anime.Nsfw != "white";
        entry.FetchedAt = now;
        if (string.IsNullOrEmpty(entry.DisplayTitle))
            entry.DisplayTitle = entry.EnglishTitle.Length > 0 ? entry.EnglishTitle : entry.Title;
        if (string.IsNullOrEmpty(entry.DisplayOverview))
            entry.DisplayOverview = entry.Synopsis;
    }

    public async Task<MalCatalogEntry> GetOrFetchAsync(
        int malId,
        string? accessToken,
        MalClient client,
        CancellationToken ct = default)
    {
        MalCatalogEntry? existing = await GetAsync(malId, ct);
        if (existing is not null
            && existing.FetchedAt > time.GetUtcNow().UtcDateTime.AddDays(-7)
            && !string.IsNullOrWhiteSpace(existing.RelatedAnimeJson)
            && existing.RelatedAnimeJson != "[]")
        {
            return existing;
        }

        try
        {
            MalAnime anime = await client.GetAnimeAsync(malId, accessToken, ct);
            return await UpsertAsync(anime, ct);
        }
        catch (Exception ex) when (existing is not null)
        {
            logger.LogDebug(ex, "Could not refresh MAL entry {MalId}; using cache", malId);
            return existing;
        }
    }

    /// <summary>Resolves TMDB artwork and display metadata for a cached MAL entry.</summary>
    public async Task<MalCatalogEntry> EnrichAsync(MalCatalogEntry entry, CancellationToken ct = default)
    {
        if (!NeedsEnrichment(entry)
            && entry.EnrichedAt is not null
            && entry.EnrichedAt > time.GetUtcNow().UtcDateTime.AddDays(-14))
        {
            return entry;
        }

        MalDatasetEntry? mapping;
        try
        {
            await dataset.EnsureLoadedAsync(ct);
            mapping = dataset.ByMalId(entry.MalId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not load the MAL mapping dataset");
            return entry;
        }
        if (mapping is null)
            return entry;

        try
        {
            if (mapping.TmdbMovieIds.Count > 0 && tmdb.IsConfigured)
            {
                TmdbMovie? movie = await tmdb.GetMovieAsync(mapping.TmdbMovieIds[0], ct);
                if (movie is not null)
                {
                    entry.TmdbId = mapping.TmdbMovieIds[0];
                    entry.TmdbKind = "movie";
                    entry.TmdbSeason = null;
                    entry.PosterUrl = TmdbService.GetPosterUrl(movie.PosterPath);
                    entry.DisplayTitle = movie.Title;
                    entry.DisplayOverview = movie.Overview;
                    entry.Year = ParseYear(movie.ReleaseDate);
                }
            }
            else if (mapping.TmdbTvId is int tmdbId && tmdb.IsConfigured)
            {
                TmdbTvShow? show = await tmdb.GetTvShowAsync(tmdbId, ct);
                TmdbTvSeason? seasonDetail = null;
                string seasonPoster = "";
                if (mapping.TmdbSeason is int season)
                {
                    seasonDetail = await tmdb.GetTvSeasonAsync(tmdbId, season, ct);
                    if (seasonDetail is not null)
                        seasonPoster = TmdbService.GetPosterUrl(seasonDetail.PosterPath);
                }

                if (show is not null)
                {
                    entry.TmdbId = tmdbId;
                    entry.TmdbKind = "tv";
                    entry.TmdbSeason = mapping.TmdbSeason;
                    entry.PosterUrl = !string.IsNullOrEmpty(seasonPoster)
                        ? seasonPoster
                        : TmdbService.GetPosterUrl(show.PosterPath);
                    // Never use TMDB's season label ("Season 1") as the title. Show the series name
                    // and append the season only when the MAL entry is one specific season.
                    entry.DisplayTitle = show.Name;
                    entry.DisplayOverview = !string.IsNullOrWhiteSpace(seasonDetail?.Overview)
                        ? seasonDetail.Overview
                        : show.Overview;
                    entry.Year = ParseYear(show.FirstAirDate);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not enrich MAL entry {MalId} from TMDB", entry.MalId);
        }

        entry.EnrichedAt = time.GetUtcNow().UtcDateTime;
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.Set<MalCatalogEntry>().Update(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>Enriches a small visible set, with bounded concurrency to respect TMDB limits.</summary>
    public async Task<IReadOnlyList<MalCatalogEntry>> EnrichManyAsync(
        IReadOnlyList<MalCatalogEntry> entries,
        CancellationToken ct = default)
    {
        SemaphoreSlim gate = new(4, 4);
        List<MalCatalogEntry> result = [];
        foreach (MalCatalogEntry entry in entries)
        {
            await gate.WaitAsync(ct);
            try
            {
                result.Add(await EnrichAsync(entry, ct));
            }
            finally
            {
                gate.Release();
            }
        }
        return result;
    }


    /// <summary>
    /// Resolves the full TMDB presentation for the detail popup: show/movie title, overview,
    /// poster, backdrop and runtime. Genres stay MAL-driven because they are more
    /// accurate for anime. Returns null when TMDB is not configured or has no mapping.
    /// </summary>
    public async Task<MalPresentation?> GetPresentationAsync(MalCatalogEntry entry, CancellationToken ct = default)
    {
        if (!tmdb.IsConfigured)
            return null;

        try
        {
            await dataset.EnsureLoadedAsync(ct);
            MalDatasetEntry? mapping = dataset.ByMalId(entry.MalId);
            if (mapping is null)
                return null;

            if (mapping.TmdbMovieIds.Count > 0)
            {
                int movieId = mapping.TmdbMovieIds[0];
                TmdbMovie? movie = await tmdb.GetMovieAsync(movieId, ct);
                if (movie is null)
                    return null;

                return new MalPresentation(
                    movie.Title,
                    movie.Overview,
                    TmdbService.GetPosterUrl(movie.PosterPath, "w500"),
                    TmdbService.GetBackdropUrl(movie.BackdropPath, "w1280"),
                    ParseYear(movie.ReleaseDate) ?? 0,
                    movie.VoteAverage > 0 ? movie.VoteAverage : null,
                    movie.Runtime,
                    movie.Status,
                    "",
                    movieId,
                    true);
            }

            if (mapping.TmdbTvId is not int showId)
                return null;

            TmdbTvDetail? show = await tmdb.GetTvDetailAsync(showId, ct);
            if (show is null)
                return null;

            TmdbTvSeason? season = mapping.TmdbSeason is int seasonNumber
                ? await tmdb.GetTvSeasonAsync(showId, seasonNumber, ct)
                : null;

            string overview = !string.IsNullOrWhiteSpace(season?.Overview) ? season.Overview : show.Overview;
            string poster = !string.IsNullOrWhiteSpace(season?.PosterPath)
                ? TmdbService.GetPosterUrl(season.PosterPath, "w500")
                : TmdbService.GetPosterUrl(show.PosterPath, "w500");

            return new MalPresentation(
                show.Name,
                overview,
                poster,
                TmdbService.GetBackdropUrl(show.BackdropPath, "w1280"),
                ParseYear(show.FirstAirDate) ?? 0,
                show.VoteAverage > 0 ? show.VoteAverage : null,
                show.EpisodeRunTime.FirstOrDefault() is int runtime && runtime > 0 ? runtime : null,
                show.Status,
                show.Networks.FirstOrDefault()?.Name ?? "",
                showId,
                false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve TMDB presentation for MAL entry {MalId}", entry.MalId);
            return null;
        }
    }

    /// <summary>True when TMDB metadata is missing or still uses a TMDB season label as its title.</summary>
    public static bool NeedsEnrichment(MalCatalogEntry entry) =>
        entry.EnrichedAt is null
        || Regex.IsMatch(entry.DisplayTitle, @"^Season\s+\d+$", RegexOptions.IgnoreCase)
        || entry.DisplayTitle.Contains(" · Season ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Card title: TMDB series/movie name plus the season marker MAL uses. MAL is the source of
    /// truth for cours, so Re:Zero's "Season 2" and "Season 2 Part 2" stay distinct even when TMDB
    /// groups them into one season.
    /// </summary>
    public static string CardTitle(MalCatalogEntry entry)
    {
        string baseTitle = !string.IsNullOrWhiteSpace(entry.DisplayTitle)
            ? entry.DisplayTitle
            : !string.IsNullOrWhiteSpace(entry.EnglishTitle) ? entry.EnglishTitle : entry.Title;

        // Older cache rows stored "Show · Season 1" before season labels moved to this formatter.
        int staleSuffix = baseTitle.IndexOf(" · Season ", StringComparison.OrdinalIgnoreCase);
        if (staleSuffix > 0)
            baseTitle = baseTitle[..staleSuffix];

        return baseTitle;
    }

    /// <summary>Season/part label shown in the standard card meta line.</summary>
    public static string? SeasonLabel(MalCatalogEntry entry)
    {
        string malTitle = !string.IsNullOrWhiteSpace(entry.EnglishTitle) ? entry.EnglishTitle : entry.Title;
        string? suffix = SeasonSuffix(malTitle);
        if (suffix is null && entry.TmdbSeason is int season && season > 1)
            suffix = $"Season {season}";
        return suffix;
    }

    /// <summary>Season/part marker extracted from the MAL title, or null when it has none.</summary>
    public static string? SeasonSuffix(string title)
    {
        Match match = Regex.Match(
            title,
            @"(?i)(?:season\s*\d+.*|\d+(?:st|nd|rd|th)\s+season.*|part\s*\d+.*|final\s+season.*|cour\s*\d+.*)$");
        return match.Success ? match.Value.Trim() : null;
    }

    public static IReadOnlyList<MalRelatedEntry> Relations(MalCatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RelatedAnimeJson))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<MalRelatedEntry>>(entry.RelatedAnimeJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static int YearOf(MalCatalogEntry entry) =>
        entry.Year ?? (DateTime.TryParse(entry.StartDate, out DateTime parsed) ? parsed.Year : 0);

    public static string Poster(MalCatalogEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.PosterUrl) ? entry.PosterUrl : entry.MainPictureUrl;

    static int? ParseYear(string? date)
        => DateTime.TryParse(date, out DateTime parsed) ? parsed.Year : null;
}
