using Microsoft.EntityFrameworkCore;
using Stackarr.Core.Notifications;

namespace Stackarr.Core.Playback;

/// <summary>Limits shared by the watchlist service and its UI.</summary>
public static class WatchlistLimits
{
    public const int MaxNameLength = 30;
    public const int MaxListsPerUser = 50;
}

/// <summary>A named, per-user list of titles the user wants to keep an eye on.</summary>
public class Watchlist
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<WatchlistItem> Items { get; set; } = [];
}

/// <summary>A title inside a watchlist. Identity is the same stable key used by watch activity.</summary>
public class WatchlistItem
{
    public int Id { get; set; }

    public int WatchlistId { get; set; }

    public Watchlist? Watchlist { get; set; }

    public string ProviderKey { get; set; } = WatchActivityServices.Tv;

    public MediaDomain Domain { get; set; }

    public string ItemKey { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Subtitle { get; set; }

    public string? PosterUrl { get; set; }

    public int? Year { get; set; }

    public int TmdbId { get; set; }

    public int TvdbId { get; set; }

    public DateTime AddedAt { get; set; }
}

public sealed record WatchlistItemRef(
    string ProviderKey,
    MediaDomain Domain,
    string ItemKey,
    string Title,
    string? Subtitle = null,
    string? PosterUrl = null,
    int? Year = null,
    int TmdbId = 0,
    int TvdbId = 0);

public sealed record WatchlistSummary(
    int Id,
    string Name,
    string? Description,
    int ItemCount,
    DateTime UpdatedAt,
    IReadOnlyList<string> PreviewPosters);

public sealed record WatchlistDetail(
    int Id,
    int UserId,
    string Name,
    string? Description,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<WatchlistItem> Items);

/// <summary>
/// User-owned watchlists. Independent of the TV/music modules so plugins can use the
/// same lists, and so staleness can grant extra retention to anything a user explicitly kept.
/// </summary>
public sealed class WatchlistService(
    IDbContextFactory<StackarrDbContext> dbFactory,
    NotificationSubscriptionService subscriptions,
    TimeProvider time)
{
    /// <summary>Raised after a list or item changes, so shelves can refresh live.</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<WatchlistSummary>> GetForUserAsync(int userId, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<Watchlist> lists = await db.Set<Watchlist>()
            .AsNoTracking()
            .Include(x => x.Items)
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        return lists.Select(list => new WatchlistSummary(
            list.Id,
            list.Name,
            list.Description,
            list.Items.Count,
            list.UpdatedAt,
            list.Items.OrderByDescending(x => x.AddedAt).Take(4).Select(x => x.PosterUrl ?? "").Where(x => x != "").ToList()))
            .ToList();
    }

    public async Task<WatchlistDetail?> GetAsync(int userId, int watchlistId, bool isAdmin = false, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Watchlist? list = await db.Set<Watchlist>()
            .AsNoTracking()
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == watchlistId && (isAdmin || x.UserId == userId), ct);
        return list is null
            ? null
            : new WatchlistDetail(
                list.Id,
                list.UserId,
                list.Name,
                list.Description,
                list.CreatedAt,
                list.UpdatedAt,
                list.Items.OrderByDescending(x => x.AddedAt).ToList());
    }

    public async Task<WatchlistDetail?> CreateAsync(
        int userId,
        string name,
        string? description = null,
        CancellationToken ct = default)
    {
        string clean = CleanName(name);
        DateTime now = time.GetUtcNow().UtcDateTime;
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        int count = await db.Set<Watchlist>().CountAsync(x => x.UserId == userId, ct);
        if (count >= WatchlistLimits.MaxListsPerUser)
            return null;

        Watchlist list = new()
        {
            UserId = userId,
            Name = await UniqueNameAsync(db, userId, clean, null, ct),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Set<Watchlist>().Add(list);
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return new WatchlistDetail(list.Id, list.UserId, list.Name, list.Description, list.CreatedAt, list.UpdatedAt, []);
    }

    public async Task<WatchlistDetail?> RenameAsync(
        int userId,
        int watchlistId,
        string name,
        string? description = null,
        CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Watchlist? list = await db.Set<Watchlist>().FirstOrDefaultAsync(x => x.Id == watchlistId && x.UserId == userId, ct);
        if (list is null)
            return null;

        list.Name = await UniqueNameAsync(db, userId, CleanName(name), watchlistId, ct);
        list.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        list.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return await GetAsync(userId, watchlistId, isAdmin: true, ct);
    }

    public async Task<bool> DeleteAsync(int userId, int watchlistId, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Watchlist? list = await db.Set<Watchlist>()
            .FirstOrDefaultAsync(x => x.Id == watchlistId && x.UserId == userId, ct);
        if (list is null)
            return false;

        List<WatchlistItem> items = await db.Set<WatchlistItem>()
            .Where(x => x.WatchlistId == list.Id)
            .ToListAsync(ct);

        db.Set<Watchlist>().Remove(list);
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();

        foreach (WatchlistItem item in items)
            await RemoveFollowIfUntrackedAsync(userId, item, ct);

        return true;
    }

    public async Task<bool> AddItemAsync(int userId, int watchlistId, WatchlistItemRef item, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.ItemKey))
            return false;

        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Watchlist? list = await db.Set<Watchlist>().FirstOrDefaultAsync(x => x.Id == watchlistId && x.UserId == userId, ct);
        if (list is null)
            return false;

        WatchlistItem? existing = await db.Set<WatchlistItem>()
            .FirstOrDefaultAsync(x => x.WatchlistId == watchlistId && x.ItemKey == item.ItemKey, ct);
        if (existing is not null)
        {
            await SubscribeForItemAsync(userId, existing, ct);
            return true;
        }

        DateTime now = time.GetUtcNow().UtcDateTime;
        WatchlistItem entry = new()
        {
            WatchlistId = watchlistId,
            ProviderKey = item.ProviderKey,
            Domain = item.Domain,
            ItemKey = item.ItemKey,
            Title = item.Title,
            Subtitle = item.Subtitle,
            PosterUrl = item.PosterUrl,
            Year = item.Year,
            TmdbId = item.TmdbId,
            TvdbId = item.TvdbId,
            AddedAt = now
        };
        db.Set<WatchlistItem>().Add(entry);
        list.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        await SubscribeForItemAsync(userId, entry, ct);
        return true;
    }

    public async Task<bool> RemoveItemAsync(int userId, int watchlistId, string itemKey, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Watchlist? list = await db.Set<Watchlist>().FirstOrDefaultAsync(x => x.Id == watchlistId && x.UserId == userId, ct);
        if (list is null)
            return false;

        WatchlistItem? item = await db.Set<WatchlistItem>()
            .FirstOrDefaultAsync(x => x.WatchlistId == watchlistId && x.ItemKey == itemKey, ct);
        if (item is null)
            return false;

        db.Set<WatchlistItem>().Remove(item);
        list.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        await RemoveFollowIfUntrackedAsync(userId, item, ct);
        return true;
    }

    /// <summary>Watchlists belonging to this user that contain the item.</summary>
    public async Task<IReadOnlyList<int>> GetContainingAsync(
        int userId,
        string providerKey,
        string itemKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
            return [];

        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<WatchlistItem>()
            .AsNoTracking()
            .Where(x => x.Watchlist!.UserId == userId && x.ProviderKey == providerKey && x.ItemKey == itemKey)
            .Select(x => x.WatchlistId)
            .ToListAsync(ct);
    }

    public async Task<bool> IsTrackedAsync(
        int userId,
        string providerKey,
        string itemKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
            return false;
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<WatchlistItem>()
            .AsNoTracking()
            .AnyAsync(x => x.Watchlist!.UserId == userId && x.ProviderKey == providerKey && x.ItemKey == itemKey, ct);
    }

    /// <summary>Every item key kept in any user's watchlist for a service (staleness protection).</summary>
    public async Task<IReadOnlySet<string>> GetTrackedItemKeysAsync(string providerKey, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<string> keys = await db.Set<WatchlistItem>()
            .AsNoTracking()
            .Where(x => x.ProviderKey == providerKey)
            .Select(x => x.ItemKey)
            .Distinct()
            .ToListAsync(ct);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// A bookmark is also a follow. Every watchlist item creates a notification subscription, with
    /// series/season bookmarks including their children so future episodes and seasons arrive.
    /// </summary>
    async Task SubscribeForItemAsync(int userId, WatchlistItem item, CancellationToken ct)
    {
        MediaEntityRef entity = ToEntity(item, out bool includeDescendants);
        await subscriptions.SubscribeAsync(
            userId,
            entity,
            new NotificationSubscriptionOptions { IncludeDescendants = includeDescendants },
            source: "watchlist",
            sourceId: item.Id.ToString(),
            ct: ct);
    }

    async Task RemoveFollowIfUntrackedAsync(int userId, WatchlistItem item, CancellationToken ct)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        bool stillTracked = await db.Set<WatchlistItem>()
            .AnyAsync(x =>
                x.Watchlist!.UserId == userId &&
                x.ProviderKey == item.ProviderKey &&
                x.ItemKey == item.ItemKey, ct);
        if (!stillTracked)
        {
            await subscriptions.RemoveIfSourceAsync(
                userId, item.ProviderKey, item.ItemKey, "watchlist", ct);
        }
    }

    static MediaEntityRef ToEntity(WatchlistItem item, out bool includeDescendants)
    {
        string? parent = null;
        List<string> ancestors = [];
        includeDescendants = false;

        if (string.Equals(item.ProviderKey, WatchActivityServices.Tv, StringComparison.OrdinalIgnoreCase) &&
            MediaItemKeys.TryGetSeriesHierarchy(item.ItemKey, out MediaKeyHierarchy hierarchy))
        {
            includeDescendants = hierarchy.IncludesChildren;
            if (hierarchy.SeasonKey is not null)
            {
                parent = hierarchy.SeasonKey;
                ancestors.Add(hierarchy.SeasonKey);
                ancestors.Add(hierarchy.SeriesKey);
            }
        }

        return new MediaEntityRef(
            item.ProviderKey,
            item.Domain,
            item.ItemKey,
            item.Title,
            item.Subtitle,
            item.PosterUrl,
            ParentItemKey: parent,
            AncestorItemKeys: ancestors);
    }

    static string CleanName(string name)
    {
        string clean = (name ?? "").Trim();
        return string.IsNullOrEmpty(clean)
            ? "Watchlist"
            : clean[..Math.Min(clean.Length, WatchlistLimits.MaxNameLength)];
    }

    static async Task<string> UniqueNameAsync(
        StackarrDbContext db,
        int userId,
        string name,
        int? exceptId,
        CancellationToken ct)
    {
        string candidate = name;
        int suffix = 2;
        while (await db.Set<Watchlist>().AnyAsync(
            x => x.UserId == userId && x.Name == candidate && (exceptId == null || x.Id != exceptId), ct))
        {
            candidate = SuffixedName(name, suffix++);
        }
        return candidate;
    }

    static string SuffixedName(string name, int suffix)
    {
        string suffixText = $" ({suffix})";
        int maxBaseLength = WatchlistLimits.MaxNameLength - suffixText.Length;
        string baseName = maxBaseLength <= 0
            ? ""
            : name[..Math.Min(name.Length, maxBaseLength)].TrimEnd();
        return string.IsNullOrEmpty(baseName) ? suffixText.Trim() : baseName + suffixText;
    }
}
