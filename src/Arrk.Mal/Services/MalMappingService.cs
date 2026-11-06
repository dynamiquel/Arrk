using Microsoft.EntityFrameworkCore;
using Arrk.Core;
using Arrk.Mal.Models;

namespace Arrk.Mal.Services;

public sealed record MalMappedPart(int MalId, int LocalEpisodeStart, int LocalEpisodeEnd, int MalEpisodeStart);

public sealed record MalMappingResult(int MalMapId, int MalId, IReadOnlyList<MalMappedPart> Parts)
{
    public bool TryMapEpisode(int localEpisode, out int malId, out int malEpisode)
    {
        foreach (MalMappedPart part in Parts)
        {
            if (localEpisode < part.LocalEpisodeStart || localEpisode > part.LocalEpisodeEnd)
                continue;

            malId = part.MalId;
            malEpisode = part.MalEpisodeStart + (localEpisode - part.LocalEpisodeStart);
            return true;
        }

        malId = 0;
        malEpisode = 0;
        return false;
    }
}

/// <summary>
/// Resolves a local TV season or movie to MAL entries. Mappings are global, because the library is
/// global, and are stored so they can be adjusted without changing the schema.
/// </summary>
public sealed class MalMappingService(
    IDbContextFactory<ArrkDbContext> dbFactory,
    MalMappingDataset dataset,
    TimeProvider time)
{
    public async Task<MalMappingResult?> ResolveSeriesAsync(
        int tmdbId,
        int tvdbId,
        int season,
        IReadOnlyDictionary<int, int> episodeCounts,
        CancellationToken ct = default)
    {
        MalMappingResult? existing = await FindSeriesAsync("tvdb", tvdbId, season, ct)
            ?? (tmdbId > 0 ? await FindSeriesAsync("tmdb", tmdbId, season, ct) : null);
        if (existing is not null)
            return existing;

        await dataset.EnsureLoadedAsync(ct);
        int localCount = episodeCounts.TryGetValue(season, out int count) ? count : 0;

        MalDatasetEntry[] candidates = [];
        string provider = "";
        int providerId = 0;
        bool seriesWide = false;

        if (tvdbId > 0)
        {
            candidates = [.. dataset.TvSeason(tvdbId, season).Where(IsSeriesEntry)];
            provider = "tvdb";
            providerId = tvdbId;
        }

        if (candidates.Length == 0 && tmdbId > 0)
        {
            candidates = [.. dataset.TmdbSeason(tmdbId, season).Where(IsSeriesEntry)];
            provider = "tmdb";
            providerId = tmdbId;
        }

        if (candidates.Length == 0 && tvdbId > 0)
        {
            candidates = [.. dataset.SeriesWideForTvdb(tvdbId).Where(IsSeriesEntry)];
            provider = "tvdb";
            providerId = tvdbId;
            seriesWide = candidates.Length > 0;
        }

        if (candidates.Length == 0 && tmdbId > 0)
        {
            candidates = [.. dataset.SeriesWideForTmdb(tmdbId).Where(IsSeriesEntry)];
            provider = "tmdb";
            providerId = tmdbId;
            seriesWide = candidates.Length > 0;
        }

        if (candidates.Length == 0 || providerId <= 0)
            return null;

        List<(MalDatasetEntry Entry, int LocalStart)> starts = [];
        foreach (MalDatasetEntry entry in candidates)
        {
            int offset = provider == "tvdb"
                ? entry.TvdbEpisodeOffset ?? 0
                : entry.TmdbEpisodeOffset ?? 0;
            int localStart = seriesWide ? 1 : offset + 1;
            if (localCount > 0 && localStart > localCount + 1)
                continue;
            starts.Add((entry, localStart));
        }

        if (starts.Count == 0)
            return null;

        starts = starts
            .OrderBy(x => x.LocalStart)
            .ThenBy(x => x.Entry.MalId)
            .ToList();

        // If the dataset has several entries starting at the same episode, only the first can own
        // that range. The next entry is usually a later part with its own offset.
        List<(MalDatasetEntry Entry, int LocalStart)> distinct = [];
        foreach ((MalDatasetEntry entry, int localStart) in starts)
        {
            if (distinct.Any(x => x.LocalStart == localStart))
                continue;
            distinct.Add((entry, localStart));
        }

        int seriesOffset = seriesWide ? SeriesOffset(episodeCounts, season) : 0;
        List<MalMappedPart> parts = [];
        for (int i = 0; i < distinct.Count; i++)
        {
            int localStart = distinct[i].LocalStart;
            int localEnd = i + 1 < distinct.Count
                ? distinct[i + 1].LocalStart - 1
                : Math.Max(localStart, localCount);
            int malStart = seriesWide
                ? seriesOffset + 1
                : 1;
            parts.Add(new MalMappedPart(
                distinct[i].Entry.MalId,
                localStart,
                localEnd,
                malStart));
        }

        MalMap map = new()
        {
            Provider = provider,
            ProviderId = providerId,
            Kind = "series",
            Season = season,
            State = seriesWide ? "series-wide" : parts.Count > 1 ? "split" : "auto",
            LocalEpisodeCount = localCount,
            UpdatedAt = time.GetUtcNow().UtcDateTime,
            Parts = parts.Select(p => new MalMapPart
            {
                MalId = p.MalId,
                LocalEpisodeStart = p.LocalEpisodeStart,
                LocalEpisodeEnd = p.LocalEpisodeEnd,
                MalEpisodeStart = p.MalEpisodeStart,
                Source = "dataset"
            }).ToList()
        };

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.Set<MalMap>().Add(map);
        await db.SaveChangesAsync(ct);
        return new MalMappingResult(map.Id, parts[0].MalId, parts);
    }

    public async Task<MalMappingResult?> ResolveMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        MalMappingResult? existing = await FindMovieAsync(tmdbId, ct);
        if (existing is not null)
            return existing;

        await dataset.EnsureLoadedAsync(ct);
        MalDatasetEntry? entry = dataset.MoviesForTmdb(tmdbId).FirstOrDefault(x => x.IsMovie)
            ?? dataset.MoviesForTmdb(tmdbId).FirstOrDefault();
        if (entry is null)
            return null;

        List<MalMappedPart> parts = [new(entry.MalId, 1, 1, 1)];
        MalMap map = new()
        {
            Provider = "tmdb",
            ProviderId = tmdbId,
            Kind = "movie",
            Season = 0,
            State = "auto",
            LocalEpisodeCount = 1,
            UpdatedAt = time.GetUtcNow().UtcDateTime,
            Parts = [new MalMapPart
            {
                MalId = entry.MalId,
                LocalEpisodeStart = 1,
                LocalEpisodeEnd = 1,
                MalEpisodeStart = 1,
                Source = "dataset"
            }]
        };

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.Set<MalMap>().Add(map);
        await db.SaveChangesAsync(ct);
        return new MalMappingResult(map.Id, entry.MalId, parts);
    }

    /// <summary>All MAL entries this local movie/series is mapped to, for the watchlist picker.</summary>
    public async Task<IReadOnlyList<int>> GetMalIdsForLocalAsync(
        int tmdbId,
        int tvdbId,
        bool movie,
        CancellationToken ct = default)
    {
        await dataset.EnsureLoadedAsync(ct);
        if (movie)
            return dataset.MoviesForTmdb(tmdbId).Select(x => x.MalId).Distinct().ToList();

        List<int> ids = [];
        if (tvdbId > 0)
            ids.AddRange(dataset.AllForTvdb(tvdbId).Where(IsSeriesEntry).Select(x => x.MalId));
        if (ids.Count == 0 && tmdbId > 0)
            ids.AddRange(dataset.AllForTmdb(tmdbId).Where(IsSeriesEntry).Select(x => x.MalId));
        return ids.Distinct().ToList();
    }

    public async Task<IReadOnlyList<int>> GetCurrentAsync(
        int userId,
        IReadOnlyList<int> malIds,
        string status,
        CancellationToken ct = default)
    {
        if (malIds.Count == 0)
            return [];
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<MalListEntry>()
            .AsNoTracking()
            .Where(x => x.UserId == userId && malIds.Contains(x.MalId) && x.Status == status)
            .Select(x => x.MalId)
            .ToListAsync(ct);
    }

    async Task<MalMappingResult?> FindSeriesAsync(
        string provider,
        int providerId,
        int season,
        CancellationToken ct)
    {
        if (providerId <= 0)
            return null;
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MalMap? map = await db.Set<MalMap>()
            .AsNoTracking()
            .Include(x => x.Parts)
            .FirstOrDefaultAsync(x =>
                x.Provider == provider && x.ProviderId == providerId && x.Kind == "series" && x.Season == season, ct);
        return ToResult(map);
    }

    async Task<MalMappingResult?> FindMovieAsync(int tmdbId, CancellationToken ct)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MalMap? map = await db.Set<MalMap>()
            .AsNoTracking()
            .Include(x => x.Parts)
            .FirstOrDefaultAsync(x =>
                x.Provider == "tmdb" && x.ProviderId == tmdbId && x.Kind == "movie" && x.Season == 0, ct);
        return ToResult(map);
    }

    static MalMappingResult? ToResult(MalMap? map)
    {
        if (map is null || map.Parts.Count == 0)
            return null;
        List<MalMappedPart> parts = map.Parts
            .OrderBy(x => x.LocalEpisodeStart)
            .Select(x => new MalMappedPart(x.MalId, x.LocalEpisodeStart, x.LocalEpisodeEnd, x.MalEpisodeStart))
            .ToList();
        return new MalMappingResult(map.Id, parts[0].MalId, parts);
    }

    static bool IsSeriesEntry(MalDatasetEntry entry) =>
        string.Equals(entry.Type, "TV", StringComparison.OrdinalIgnoreCase)
        || string.Equals(entry.Type, "ONA", StringComparison.OrdinalIgnoreCase);

    static int SeriesOffset(IReadOnlyDictionary<int, int> counts, int season)
    {
        int offset = 0;
        foreach ((int number, int count) in counts)
        {
            if (number > 0 && number < season)
                offset += Math.Max(0, count);
        }
        return offset;
    }
}
