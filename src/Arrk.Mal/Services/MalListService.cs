using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Mal.Clients;
using Arrk.Mal.Models;

namespace Arrk.Mal.Services;

public sealed record MalEntryView(MalListEntry Entry, MalCatalogEntry Catalog)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Catalog.DisplayTitle) ? Catalog.Title : Catalog.DisplayTitle;
    public string Poster => MalCatalogService.Poster(Catalog);
    public string StatusLabel => MalStatuses.Label(Entry.Status);
}

public sealed record MalListUpdate(
    string? Status = null,
    int? Score = null,
    int? WatchedEpisodes = null,
    string? Tags = null,
    string? Comments = null,
    bool? IsRewatching = null);

public static class MalStatuses
{
    public const string Watching = "watching";
    public const string Completed = "completed";
    public const string OnHold = "on_hold";
    public const string Dropped = "dropped";
    public const string PlanToWatch = "plan_to_watch";

    public static readonly string[] All = [Watching, Completed, OnHold, Dropped, PlanToWatch];

    public static string Label(string status) => status switch
    {
        Watching => "Watching",
        Completed => "Completed",
        OnHold => "On hold",
        Dropped => "Dropped",
        PlanToWatch => "Plan to watch",
        _ => status
    };
}

/// <summary>
/// The user's cached MAL list and all list mutations. MAL remains the source of truth; the cache
/// exists so pages and the watchlist shelf render instantly and work offline.
/// </summary>
public sealed class MalListService(
    IDbContextFactory<ArrkDbContext> dbFactory,
    MalAccountService accounts,
    MalClient client,
    MalCatalogService catalog,
    TimeProvider time,
    ILogger<MalListService> logger)
{
    public event Action? Changed;

    public async Task<IReadOnlyList<MalEntryView>> GetEntriesAsync(
        int userId,
        string? status = null,
        string? search = null,
        CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<MalListEntry> query = db.Set<MalListEntry>().AsNoTracking().Where(x => x.UserId == userId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status);

        List<MalListEntry> entries = await query.ToListAsync(ct);
        List<int> ids = entries.Select(x => x.MalId).ToList();
        Dictionary<int, MalCatalogEntry> catalogs = ids.Count == 0
            ? []
            : await db.Set<MalCatalogEntry>()
                .AsNoTracking()
                .Where(x => ids.Contains(x.MalId))
                .ToDictionaryAsync(x => x.MalId, ct);

        IEnumerable<MalEntryView> views = entries
            .Where(x => catalogs.ContainsKey(x.MalId))
            .Select(x => new MalEntryView(x, catalogs[x.MalId]));

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim();
            views = views.Where(x =>
                x.Catalog.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Catalog.EnglishTitle.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Catalog.Synonyms.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return views
            .OrderBy(x => Array.IndexOf(MalStatuses.All, x.Entry.Status))
            .ThenBy(x => x.DisplayTitle)
            .ToList();
    }

    public async Task<MalEntryView?> GetEntryAsync(int userId, int malId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MalListEntry? entry = await db.Set<MalListEntry>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MalId == malId, ct);
        MalCatalogEntry? catalogEntry = await db.Set<MalCatalogEntry>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.MalId == malId, ct);
        return entry is null || catalogEntry is null ? null : new MalEntryView(entry, catalogEntry);
    }

    public async Task<IReadOnlyList<MalEntryView>> GetPlanToWatchAsync(
        int userId,
        bool movies,
        CancellationToken ct = default)
    {
        IReadOnlyList<MalEntryView> entries = await GetEntriesAsync(userId, MalStatuses.PlanToWatch, ct: ct);
        return entries
            .Where(x => movies
                ? string.Equals(x.Catalog.MediaType, "movie", StringComparison.OrdinalIgnoreCase)
                : !string.Equals(x.Catalog.MediaType, "movie", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Pulls the complete list from MAL and reconciles the cache.</summary>
    public async Task PullAsync(int userId, CancellationToken ct = default)
    {
        MalAccount? account = await accounts.GetAsync(userId, ct);
        if (account is null || account.NeedsReauth)
            return;

        string accessToken = await accounts.GetAccessTokenAsync(account, ct);
        Dictionary<int, (MalAnime Anime, MalListStatus Status)> pulled = [];
        string? next = null;
        do
        {
            MalListPage page = await client.GetListPageAsync(accessToken, next, ct);
            foreach (MalListData row in page.Data)
                pulled[row.Node.Id] = (row.Node, row.ListStatus);
            next = page.Paging?.Next;
        }
        while (!string.IsNullOrWhiteSpace(next) && pulled.Count < 10000);

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MalListEntry> existing = await db.Set<MalListEntry>()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
        Dictionary<int, MalListEntry> byMalId = existing.ToDictionary(x => x.MalId);
        DateTime now = time.GetUtcNow().UtcDateTime;

        foreach ((int malId, (MalAnime anime, MalListStatus status)) in pulled)
        {
            if (!byMalId.TryGetValue(malId, out MalListEntry? entry))
            {
                entry = new MalListEntry { UserId = userId, MalId = malId };
                db.Set<MalListEntry>().Add(entry);
                byMalId[malId] = entry;
            }

            entry.Status = string.IsNullOrEmpty(status.Status) ? MalStatuses.PlanToWatch : status.Status;
            entry.Score = Math.Clamp(status.Score, 0, 10);
            entry.WatchedEpisodes = Math.Max(0, status.WatchedEpisodes);
            entry.IsRewatching = status.IsRewatching;
            entry.Tags = string.Join(", ", status.Tags);
            entry.Comments = status.Comments ?? "";
            entry.MalUpdatedAt = status.UpdatedAt;
            entry.SyncedAt = now;
        }

        List<int> removed = existing.Select(x => x.MalId).Where(id => !pulled.ContainsKey(id)).ToList();
        if (removed.Count > 0)
            db.Set<MalListEntry>().RemoveRange(existing.Where(x => removed.Contains(x.MalId)));

        await db.SaveChangesAsync(ct);
        await catalog.UpsertManyAsync(pulled.Values.Select(x => x.Anime).ToList(), ct);

        await accounts.TouchPulledAsync(userId, ct);
        Changed?.Invoke();
        logger.LogDebug("Pulled {Count} MyAnimeList entries for user {UserId}", pulled.Count, userId);
    }

    public async Task EnsureFreshAsync(int userId, TimeSpan maxAge, CancellationToken ct = default)
    {
        MalAccount? account = await accounts.GetAsync(userId, ct);
        if (account is null || account.NeedsReauth)
            return;
        if (account.LastPulledAt is { } at && time.GetUtcNow().UtcDateTime - at < maxAge)
            return;

        try
        {
            await PullAsync(userId, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not refresh MyAnimeList list for user {UserId}", userId);
        }
    }

    public async Task<IReadOnlyList<MalCatalogEntry>> SearchAsync(
        int userId,
        string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !client.IsConfigured)
            return [];

        MalAccount? account = await accounts.GetAsync(userId, ct);
        string? token = null;
        if (account is not null && !account.NeedsReauth)
        {
            try
            {
                token = await accounts.GetAccessTokenAsync(account, ct);
            }
            catch
            {
                token = null;
            }
        }

        List<MalAnime> results = await client.SearchAnimeAsync(query.Trim(), token, ct);
        List<MalCatalogEntry> entries = [];
        foreach (MalAnime anime in results)
            entries.Add(await catalog.UpsertAsync(anime, ct));

        return await catalog.EnrichManyAsync(entries.Take(24).ToList(), ct);
    }

    public async Task<MalEntryView?> UpdateAsync(
        int userId,
        int malId,
        MalListUpdate update,
        CancellationToken ct = default)
    {
        MalAccount? account = await accounts.GetAsync(userId, ct)
            ?? throw new InvalidOperationException("Link MyAnimeList first.");
        string token = await accounts.GetAccessTokenAsync(account, ct);

        Dictionary<string, string> form = [];
        if (update.Status is not null)
            form["status"] = update.Status;
        if (update.Score is int score)
            form["score"] = Math.Clamp(score, 0, 10).ToString();
        if (update.WatchedEpisodes is int watched)
            form["num_watched_episodes"] = Math.Max(0, watched).ToString();
        if (update.Tags is not null)
            form["tags"] = update.Tags;
        if (update.Comments is not null)
            form["comments"] = update.Comments;
        if (update.IsRewatching is bool rewatching)
            form["is_rewatching"] = rewatching ? "true" : "false";

        MalListStatus result = await client.UpdateListStatusAsync(token, malId, form, ct);
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MalListEntry? entry = await db.Set<MalListEntry>()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MalId == malId, ct);
        if (entry is null)
        {
            entry = new MalListEntry { UserId = userId, MalId = malId };
            db.Set<MalListEntry>().Add(entry);
        }

        entry.Status = string.IsNullOrEmpty(result.Status)
            ? update.Status ?? entry.Status
            : result.Status;
        entry.Score = Math.Clamp(result.Score, 0, 10);
        entry.WatchedEpisodes = Math.Max(0, result.WatchedEpisodes);
        entry.IsRewatching = result.IsRewatching;
        entry.Tags = string.Join(", ", result.Tags);
        entry.Comments = result.Comments ?? "";
        entry.MalUpdatedAt = result.UpdatedAt;
        entry.SyncedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        MalCatalogEntry? catalogEntry = await catalog.GetAsync(malId, ct);
        if (catalogEntry is null)
        {
            MalAnime anime = await client.GetAnimeAsync(malId, token, ct);
            catalogEntry = await catalog.UpsertAsync(anime, ct);
        }

        Changed?.Invoke();
        return new MalEntryView(entry, catalogEntry);
    }

    public async Task RemoveAsync(int userId, int malId, CancellationToken ct = default)
    {
        MalAccount? account = await accounts.GetAsync(userId, ct);
        if (account is null)
            return;
        string token = await accounts.GetAccessTokenAsync(account, ct);
        await client.DeleteListEntryAsync(token, malId, ct);

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await db.Set<MalListEntry>()
            .Where(x => x.UserId == userId && x.MalId == malId)
            .ExecuteDeleteAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>Adds an entry straight to the user's Plan to Watch list.</summary>
    public Task AddPlanToWatchAsync(int userId, int malId, CancellationToken ct = default)
        => UpdateAsync(userId, malId, new MalListUpdate(Status: MalStatuses.PlanToWatch, Score: 0), ct);

    public async Task<MalCatalogEntry?> GetCatalogAsync(int malId, CancellationToken ct = default) =>
        await catalog.GetAsync(malId, ct);

    public async Task<MalCatalogEntry?> GetDetailsAsync(int userId, int malId, CancellationToken ct = default)
    {
        MalAccount? account = await accounts.GetAsync(userId, ct);
        string? token = null;
        if (account is not null && !account.NeedsReauth)
        {
            try
            {
                token = await accounts.GetAccessTokenAsync(account, ct);
            }
            catch
            {
                token = null;
            }
        }

        MalCatalogEntry entry = await catalog.GetOrFetchAsync(malId, token, client, ct);
        return await catalog.EnrichAsync(entry, ct);
    }
}
