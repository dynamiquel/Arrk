using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Models;
using Arrk.Core.Playback;
using Arrk.Mal.Models;
using Arrk.Tvarr.Services;

namespace Arrk.Mal.Services;

/// <summary>
/// Pushes completed local watch activity to MyAnimeList. This is one-way: Arrk
/// tells MAL what was watched and what score the user set, never the reverse.
/// </summary>
public sealed class MalSyncService(
    IDbContextFactory<ArrkDbContext> dbFactory,
    MalAccountService accounts,
    MalListService lists,
    MalCatalogService catalog,
    MalMappingService mappings,
    ITvCatalog library,
    TimeProvider time,
    ILogger<MalSyncService> logger)
{
    public async Task SyncAllAsync(CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<int> userIds = await db.Set<MalAccount>()
            .AsNoTracking()
            .Where(x => x.SyncEnabled && !x.NeedsReauth)
            .Select(x => x.UserId)
            .ToListAsync(ct);

        foreach (int userId in userIds)
        {
            try
            {
                bool hasNew = await HasNewCompletedActivityAsync(userId, ct);
                if (!hasNew)
                    continue;
                await SyncUserAsync(userId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MyAnimeList sync failed for user {UserId}", userId);
                await accounts.RecordErrorAsync(userId, ex.Message, ct);
            }
        }
    }

    public async Task<string> SyncUserAsync(int userId, CancellationToken ct = default)
    {
        MalAccount? account = await accounts.GetAsync(userId, ct)
            ?? throw new InvalidOperationException("Link MyAnimeList first.");
        if (account.NeedsReauth)
            return "Reconnect MyAnimeList first.";
        if (!account.SyncEnabled)
            return "Sync is disabled for this account.";

        await lists.EnsureFreshAsync(userId, TimeSpan.FromMinutes(5), ct);

        DateTime since = (account.LastSyncedAt ?? time.GetUtcNow().UtcDateTime.AddDays(-30)).AddDays(-7);
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<WatchActivityEntry> completed = await db.Set<WatchActivityEntry>()
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.Completed && x.CompletedAt != null && x.CompletedAt >= since)
            .OrderByDescending(x => x.CompletedAt)
            .Take(5000)
            .ToListAsync(ct);

        Dictionary<int, MalListEntry> entries = await db.Set<MalListEntry>()
            .Where(x => x.UserId == userId)
            .ToDictionaryAsync(x => x.MalId, ct);
        Dictionary<int, MalCatalogEntry> catalogs = await db.Set<MalCatalogEntry>()
            .AsNoTracking()
            .ToDictionaryAsync(x => x.MalId, ct);

        (IReadOnlyDictionary<int, TvSeries> ByTmdb, IReadOnlyDictionary<int, TvSeries> ByTvdb) librarySeries;
        try
        {
            librarySeries = await library.GetLibrarySeriesAsync(ct: ct);
        }
        catch
        {
            librarySeries = (new Dictionary<int, TvSeries>(), new Dictionary<int, TvSeries>());
        }

        int updated = 0;
        int skipped = 0;
        int failed = 0;

        foreach (IGrouping<(int TmdbId, int TvdbId, int Season, string ItemType), WatchActivityEntry> group in
                 completed.GroupBy(x => (x.TmdbId, x.TvdbId, x.SeasonNumber, x.ItemType)))
        {
            try
            {
                if (string.Equals(group.Key.ItemType, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    if (await SyncMovieAsync(userId, group.Key.TmdbId, entries, ct))
                        updated++;
                    else
                        skipped++;
                    continue;
                }

                if (!string.Equals(group.Key.ItemType, "episode", StringComparison.OrdinalIgnoreCase))
                    continue;

                int maxEpisode = group.Max(x => x.EpisodeNumber);
                if (maxEpisode <= 0)
                    continue;

                IReadOnlyDictionary<int, int> seasonCounts = SeasonCounts(
                    librarySeries.ByTmdb, librarySeries.ByTvdb, group.Key.TmdbId, group.Key.TvdbId);

                MalMappingResult? mapping = await mappings.ResolveSeriesAsync(
                    group.Key.TmdbId,
                    group.Key.TvdbId,
                    group.Key.Season,
                    seasonCounts,
                    ct);
                if (mapping is null || !mapping.TryMapEpisode(maxEpisode, out int malId, out int malEpisode))
                {
                    skipped++;
                    continue;
                }

                if (await SyncEpisodeAsync(userId, malId, malEpisode, entries, catalogs, ct))
                    updated++;
                else
                    skipped++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Could not sync a MyAnimeList entry for user {UserId}", userId);
            }
        }

        DateTime now = time.GetUtcNow().UtcDateTime;
        string summary = $"{updated} updated · {skipped} unchanged · {failed} failed";
        await accounts.SetLastSyncAsync(userId, now, summary, ct);
        return summary;
    }

    async Task<bool> SyncEpisodeAsync(
        int userId,
        int malId,
        int malEpisode,
        Dictionary<int, MalListEntry> entries,
        Dictionary<int, MalCatalogEntry> catalogs,
        CancellationToken ct)
    {
        entries.TryGetValue(malId, out MalListEntry? entry);
        if (entry is not null)
        {
            if (entry.Status is MalStatuses.OnHold or MalStatuses.Dropped or MalStatuses.Completed)
                return false;
            if (malEpisode <= entry.WatchedEpisodes)
                return false;
        }

        string status = malEpisode > 0 ? MalStatuses.Watching : MalStatuses.PlanToWatch;
        if (catalogs.TryGetValue(malId, out MalCatalogEntry? catalogEntry)
            && catalogEntry.NumEpisodes > 0
            && malEpisode >= catalogEntry.NumEpisodes)
        {
            status = MalStatuses.Completed;
        }
        else if (entry?.Status == MalStatuses.Watching)
        {
            status = MalStatuses.Watching;
        }

        MalEntryView? result = await lists.UpdateAsync(
            userId,
            malId,
            new MalListUpdate(Status: status, WatchedEpisodes: malEpisode),
            ct);
        if (result is not null)
            entries[malId] = result.Entry;
        return result is not null;
    }

    async Task<bool> SyncMovieAsync(
        int userId,
        int tmdbId,
        Dictionary<int, MalListEntry> entries,
        CancellationToken ct)
    {
        if (tmdbId <= 0)
            return false;
        MalMappingResult? mapping = await mappings.ResolveMovieAsync(tmdbId, ct);
        if (mapping is null)
            return false;

        int malId = mapping.Parts[0].MalId;
        if (entries.TryGetValue(malId, out MalListEntry? entry)
            && entry.Status is MalStatuses.Completed or MalStatuses.OnHold or MalStatuses.Dropped)
            return false;

        MalEntryView? result = await lists.UpdateAsync(
            userId,
            malId,
            new MalListUpdate(Status: MalStatuses.Completed, WatchedEpisodes: 1),
            ct);
        if (result is not null)
            entries[malId] = result.Entry;
        return result is not null;
    }

    async Task<bool> HasNewCompletedActivityAsync(int userId, CancellationToken ct)
    {
        MalAccount? account = await accounts.GetAsync(userId, ct);
        if (account is null)
            return false;
        DateTime since = (account.LastSyncedAt ?? time.GetUtcNow().UtcDateTime.AddDays(-30)).AddDays(-7);

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<WatchActivityEntry>()
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId
                           && x.Completed
                           && x.CompletedAt != null
                           && x.CompletedAt >= since, ct);
    }

    static IReadOnlyDictionary<int, int> SeasonCounts(
        IReadOnlyDictionary<int, TvSeries> byTmdb,
        IReadOnlyDictionary<int, TvSeries> byTvdb,
        int tmdbId,
        int tvdbId)
    {
        TvSeries? series = null;
        if (tmdbId > 0 && byTmdb.TryGetValue(tmdbId, out TvSeries? byTmdbSeries))
            series = byTmdbSeries;
        else if (tvdbId > 0 && byTvdb.TryGetValue(tvdbId, out TvSeries? byTvdbSeries))
            series = byTvdbSeries;

        if (series is null)
            return new Dictionary<int, int>();

        return series.Seasons
            .Where(x => x.SeasonNumber > 0)
            .ToDictionary(
                x => x.SeasonNumber,
                x => x.EpisodeCount > 0 ? x.EpisodeCount : x.TotalEpisodeCount);
    }
}
