using System.Text;
using Microsoft.EntityFrameworkCore;
using Arrk.Core.Models;
using Arrk.Notifications;

namespace Arrk.Core.Events;

/// <summary>
/// EF Core backed event store. Uses an <see cref="IDbContextFactory{TContext}"/> so it can
/// be used from singletons and background jobs without lifetime issues.
/// </summary>
public sealed class EfEventStore(IDbContextFactory<ArrkDbContext> dbContextFactory) : IEventStore
{
    public async Task RecordAsync(SystemEventRecord record, CancellationToken ct = default)
        => await RecordAsync([record], ct);

    public async Task RecordAsync(IEnumerable<SystemEventRecord> records, CancellationToken ct = default)
    {
        List<SystemEvent> entities = records.Select(ToEntity).ToList();
        if (entities.Count == 0)
            return;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        db.SystemEvents.AddRange(entities);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventQuery query, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        IQueryable<SystemEvent> q = db.SystemEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Category))
            q = q.Where(e => e.Category == query.Category);

        if (!string.IsNullOrWhiteSpace(query.EventType))
            q = q.Where(e => e.EventType == query.EventType);

        if (query.MinSeverity is { } min)
        {
            // Severity is stored as a string; compare using the enum ordinal order.
            string[] allowed = Enum.GetValues<EventSeverity>()
                .Where(s => s >= min)
                .Select(s => s.ToString())
                .ToArray();
            q = q.Where(e => allowed.Contains(e.Severity));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string search = query.Search;
            q = q.Where(e =>
                e.Message.Contains(search) ||
                (e.SourceName != null && e.SourceName.Contains(search)) ||
                (e.Source != null && e.Source.Contains(search)) ||
                (e.Topic != null && e.Topic.Contains(search)) ||
                e.EventType.Contains(search));
        }

        if (query.Since is { } since)
            q = q.Where(e => e.Timestamp >= since);

        int limit = query.Limit <= 0 ? 500 : Math.Min(query.Limit, 5000);

        return await q.OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.SystemEvents.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(ct);
    }

    public async Task<int> PurgeAsync(
        DateTime defaultCutoff,
        IReadOnlyDictionary<string, DateTime?> topicCutoffs,
        CancellationToken ct = default)
    {
        List<(string Pattern, DateTime? Cutoff)> topics = topicCutoffs
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .Select(kv => (Pattern: kv.Key.Trim(), Cutoff: kv.Value))
            .OrderByDescending(t => Specificity(t.Pattern))
            .ToList();

        if (defaultCutoff == DateTime.MinValue && topics.All(t => t.Cutoff is null))
            return 0;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        int removed = 0;
        List<string> moreSpecificPatterns = [];

        // Most specific first: a narrower topic (job.metrics.#) keeps its own cutoff instead of
        // being swallowed by a broader one (job.#).
        foreach ((string pattern, DateTime? cutoff) in topics)
        {
            string likePattern = ToLikePattern(pattern);
            if (cutoff is { } topicCutoff)
            {
                IQueryable<SystemEvent> query = db.SystemEvents.Where(e =>
                    e.Topic != null &&
                    EF.Functions.Like(e.Topic, likePattern) &&
                    e.Timestamp < topicCutoff);

                foreach (string narrower in moreSpecificPatterns)
                {
                    string captured = narrower;
                    query = query.Where(e => !EF.Functions.Like(e.Topic!, captured));
                }

                removed += await query.ExecuteDeleteAsync(ct);
            }

            moreSpecificPatterns.Add(likePattern);
        }

        if (defaultCutoff > DateTime.MinValue)
        {
            IQueryable<SystemEvent> query = db.SystemEvents.Where(e => e.Timestamp < defaultCutoff);
            foreach (string specific in moreSpecificPatterns)
            {
                string captured = specific;
                query = query.Where(e => e.Topic == null || !EF.Functions.Like(e.Topic, captured));
            }

            removed += await query.ExecuteDeleteAsync(ct);
        }

        return removed;
    }

    public async Task<IReadOnlyList<string>> GetEventTypesAsync(string? category = null, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        IQueryable<SystemEvent> q = db.SystemEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(e => e.Category == category);

        return await q.Select(e => e.EventType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);
    }

    static SystemEvent ToEntity(SystemEventRecord record) => new()
    {
        Timestamp = record.Timestamp,
        Category = record.Category,
        EventType = record.EventType,
        Topic = record.Topic ?? record.Notification?.Key,
        Severity = record.Severity.ToString(),
        Message = record.Message,
        Source = record.Source,
        SourceName = record.SourceName,
        Data = record.Data
    };

    static int Specificity(string pattern) =>
        pattern.Count(c => c == '.') * 1000 + pattern.Length;

    static string ToLikePattern(string pattern)
    {
        string normalized = pattern.Trim();
        if (normalized is "" or "#" or "*")
            return "%";

        StringBuilder builder = new(normalized.Length);
        foreach (char c in normalized)
        {
            builder.Append(c switch
            {
                '#' or '*' => '%',
                '+' => '_',
                _ => c
            });
        }

        return builder.ToString();
    }
}
