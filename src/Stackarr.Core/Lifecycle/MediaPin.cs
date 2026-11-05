using Microsoft.EntityFrameworkCore;

namespace Stackarr.Core.Lifecycle;

/// <summary>
/// An admin's explicit "never stale" marker for one managed item (a movie, a series, a season, an
/// album). Pinned items are excluded from staleness and automatic cleanup until unpinned.
/// </summary>
public class MediaPin
{
    public long Id { get; set; }

    public string ProviderKey { get; set; } = "";

    public string ItemKey { get; set; } = "";

    public string? Title { get; set; }

    public DateTime PinnedAt { get; set; }

    public string? PinnedBy { get; set; }
}

/// <summary>
/// Shared pin state. Admins pin/unpin media through the UI; the lifecycle evaluator reads it and
/// forces pinned items (and everything under a pinned parent, such as every season of a pinned
/// series) to stay fresh.
/// </summary>
public sealed class MediaPinService(
    IDbContextFactory<StackarrDbContext> dbFactory,
    TimeProvider time)
{
    public async Task<bool> IsPinnedAsync(string providerKey, string itemKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
            return false;

        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<MediaPin>()
            .AsNoTracking()
            .AnyAsync(x => x.ProviderKey == providerKey && x.ItemKey == itemKey, ct);
    }

    public async Task<IReadOnlySet<string>> GetPinnedKeysAsync(string providerKey, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<string> keys = await db.Set<MediaPin>()
            .AsNoTracking()
            .Where(x => x.ProviderKey == providerKey)
            .Select(x => x.ItemKey)
            .ToListAsync(ct);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> ToggleAsync(
        string providerKey,
        string itemKey,
        string? title,
        string? username,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
            return false;

        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MediaPin? pin = await db.Set<MediaPin>()
            .FirstOrDefaultAsync(x => x.ProviderKey == providerKey && x.ItemKey == itemKey, ct);
        if (pin is not null)
        {
            db.Set<MediaPin>().Remove(pin);
            await db.SaveChangesAsync(ct);
            return false;
        }

        db.Set<MediaPin>().Add(new MediaPin
        {
            ProviderKey = providerKey,
            ItemKey = itemKey,
            Title = title,
            PinnedAt = time.GetUtcNow().UtcDateTime,
            PinnedBy = username
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task UnpinAsync(string providerKey, string itemKey, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await db.Set<MediaPin>()
            .Where(x => x.ProviderKey == providerKey && x.ItemKey == itemKey)
            .ExecuteDeleteAsync(ct);
    }
}
