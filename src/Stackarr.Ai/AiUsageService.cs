using Microsoft.EntityFrameworkCore;
using Stackarr.Ai.Data;
using Stackarr.Core;

namespace Stackarr.Ai;

/// <summary>A roll-up of AI usage over a window, grouped by feature.</summary>
public sealed record AiUsageSummary(
    DateTime Since,
    int Calls,
    long PromptTokens,
    long CompletionTokens,
    int Failures,
    int CacheHits,
    long DurationMs,
    IReadOnlyList<AiUsageFeatureSummary> Features)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
}

public sealed record AiUsageFeatureSummary(
    string Feature,
    int Calls,
    long PromptTokens,
    long CompletionTokens,
    int Failures,
    int CacheHits,
    long DurationMs,
    DateTime? LastUsedAt)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
    public long AverageDurationMs => Calls == 0 ? 0 : DurationMs / Calls;
}

/// <summary>
/// Durable AI usage accounting. Every call (including failures and cache hits) lands here so the
/// admin page can answer "what is AI costing us, and which feature is spending it?" without
/// requiring prompt logging.
/// </summary>
public sealed class AiUsageService(
    IDbContextFactory<StackarrDbContext> dbContextFactory,
    TimeProvider time)
{
    public async Task RecordAsync(AiUsageRecord record, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        db.Set<AiUsageRecord>().Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<AiUsageSummary> GetSummaryAsync(DateTime since, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        List<AiUsageRecord> rows = await db.Set<AiUsageRecord>()
            .AsNoTracking()
            .Where(x => x.Timestamp >= since)
            .ToListAsync(ct);

        List<AiUsageFeatureSummary> features = rows
            .GroupBy(x => x.Feature)
            .Select(g => new AiUsageFeatureSummary(
                g.Key,
                g.Count(),
                g.Sum(x => x.PromptTokens),
                g.Sum(x => x.CompletionTokens),
                g.Count(x => !x.Success),
                g.Count(x => x.CacheHit),
                g.Sum(x => x.DurationMs),
                g.Max(x => (DateTime?)x.Timestamp)))
            .OrderByDescending(x => x.Calls)
            .ToList();

        return new AiUsageSummary(
            since,
            rows.Count,
            rows.Sum(x => x.PromptTokens),
            rows.Sum(x => x.CompletionTokens),
            rows.Count(x => !x.Success),
            rows.Count(x => x.CacheHit),
            rows.Sum(x => x.DurationMs),
            features);
    }

    public async Task<IReadOnlyList<AiUsageRecord>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<AiUsageRecord>()
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
    }

    public async Task<int> CountSinceAsync(DateTime since, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<AiUsageRecord>().CountAsync(x => x.Timestamp >= since, ct);
    }

    public async Task<long> TokensSinceAsync(DateTime since, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<AiUsageRecord>()
            .Where(x => x.Timestamp >= since)
            .SumAsync(x => x.PromptTokens + x.CompletionTokens, ct);
    }

    /// <summary>How many calls one user made for a feature since a point in time.</summary>
    public async Task<int> CountForUserAsync(int userId, string feature, DateTime since, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<AiUsageRecord>()
            .CountAsync(x => x.UserId == userId && x.Feature == feature && x.Timestamp >= since, ct);
    }

    /// <summary>Tokens one user spent on a feature since a point in time.</summary>
    public async Task<long> TokensForUserAsync(int userId, string feature, DateTime since, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<AiUsageRecord>()
            .Where(x => x.UserId == userId && x.Feature == feature && x.Timestamp >= since)
            .SumAsync(x => x.PromptTokens + x.CompletionTokens, ct);
    }

    public async Task<int> PurgeBeforeAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<AiUsageRecord>()
            .Where(x => x.Timestamp < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Builds the record for a completed call, applying the privacy setting.</summary>
    public AiUsageRecord CreateRecord(
        AiConfig config,
        string feature,
        string operation,
        AiModelRole role,
        long promptTokens,
        long completionTokens,
        long durationMs,
        bool success,
        bool cacheHit,
        int? userId,
        string? error,
        string? promptText,
        string? responseText)
    {
        return new AiUsageRecord
        {
            Timestamp = time.GetUtcNow().UtcDateTime,
            Feature = feature,
            Operation = operation,
            Model = role == AiModelRole.Fast ? config.ResolveFastModel() : config.ResolveChatModel(),
            Provider = config.Provider,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            DurationMs = durationMs,
            Success = success,
            CacheHit = cacheHit,
            UserId = userId,
            Error = error,
            PromptText = config.Privacy.StorePromptText ? promptText : null,
            ResponseText = config.Privacy.StorePromptText ? responseText : null
        };
    }
}
