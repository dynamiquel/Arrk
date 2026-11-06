using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Arrk.Ai.Data;
using Arrk.Core;

namespace Arrk.Ai;

/// <summary>
/// Tiny SQLite-backed response cache for bounded, repeatable prompts (release ranking,
/// recommendations, discovery parsing). Callers supply the key; the cache never inspects prompts.
/// </summary>
public sealed class AiCache(
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    TimeProvider time)
{
    /// <summary>Stable cache key for a feature/model/prompt triple.</summary>
    public static string Key(string feature, string model, string prompt)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{feature}|{model}|{prompt}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<string?> TryGetAsync(string key, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        AiCacheEntry? entry = await db.Set<AiCacheEntry>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key, ct);

        if (entry is null)
            return null;

        if (entry.ExpiresAt <= time.GetUtcNow().UtcDateTime)
        {
            await db.Set<AiCacheEntry>().Where(x => x.Key == key).ExecuteDeleteAsync(ct);
            return null;
        }

        return entry.Value;
    }

    public async Task SetAsync(string key, string feature, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        if (ttl <= TimeSpan.Zero)
            return;

        DateTime now = time.GetUtcNow().UtcDateTime;
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        AiCacheEntry? existing = await db.Set<AiCacheEntry>().FirstOrDefaultAsync(x => x.Key == key, ct);
        if (existing is null)
        {
            db.Set<AiCacheEntry>().Add(new AiCacheEntry
            {
                Key = key,
                Feature = feature,
                Value = value,
                CreatedAt = now,
                ExpiresAt = now.Add(ttl)
            });
        }
        else
        {
            existing.Value = value;
            existing.Feature = feature;
            existing.ExpiresAt = now.Add(ttl);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<(int Entries, long Bytes)> GetStatsAsync(CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        List<string> values = await db.Set<AiCacheEntry>()
            .AsNoTracking()
            .Select(x => x.Value)
            .ToListAsync(ct);

        return (values.Count, values.Sum(v => (long)Encoding.UTF8.GetByteCount(v)));
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<AiCacheEntry>()
            .Where(x => x.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.Set<AiCacheEntry>().ExecuteDeleteAsync(ct);
    }
}
