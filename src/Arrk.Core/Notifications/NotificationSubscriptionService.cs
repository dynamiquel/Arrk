using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Arrk.Media.Abstractions;

namespace Arrk.Core.Notifications;

/// <summary>
/// Owns per-user media subscriptions and resolves which users should receive an entity event.
/// Subscriptions are independent of any delivery channel, so Discord DMs, email or a future push
/// transport all share the same interest model.
/// </summary>
public sealed class NotificationSubscriptionService(
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    TimeProvider time,
    ILogger<NotificationSubscriptionService> logger)
{
    public async Task<NotificationSubscription> SubscribeAsync(
        int userId,
        MediaEntityRef entity,
        NotificationSubscriptionOptions? options = null,
        string source = "manual",
        string? sourceId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.ItemKey);

        NotificationSubscriptionOptions settings = options ?? new NotificationSubscriptionOptions();
        DateTime now = time.GetUtcNow().UtcDateTime;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        NotificationSubscription? subscription = await db.Set<NotificationSubscription>()
            .FirstOrDefaultAsync(x =>
                x.UserId == userId &&
                x.ProviderKey == entity.ProviderKey &&
                x.ItemKey == entity.ItemKey, ct);

        bool created = subscription is null;
        if (created)
        {
            subscription = new NotificationSubscription
            {
                UserId = userId,
                ProviderKey = entity.ProviderKey,
                Domain = entity.Domain,
                ItemKey = entity.ItemKey,
                CreatedAt = now
            };
            db.Set<NotificationSubscription>().Add(subscription);
        }

        subscription.Domain = entity.Domain;
        subscription.Title = entity.Title;
        subscription.Subtitle = entity.Subtitle;
        subscription.PosterUrl = entity.PosterUrl;

        // An existing follow already carries the user's category choices and source. Re-subscribing
        // (for example when a request is approved) must not reset them.
        if (created)
        {
            subscription.IncludeDescendants = settings.IncludeDescendants;
            subscription.NotifyStatus = settings.Status;
            subscription.NotifyDownload = settings.Download;
            subscription.NotifyNewContent = settings.NewContent;
            subscription.NotifyProblems = settings.Problems;
            subscription.NotifyLifecycle = settings.Lifecycle;
            subscription.Source = source;
            subscription.SourceId = sourceId;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent subscribe won the unique index race; reload the row.
            logger.LogDebug(ex, "Concurrent subscription insert for {User}/{Key}", userId, entity.ItemKey);
            await using ArrkDbContext reload = await dbContextFactory.CreateDbContextAsync(ct);
            subscription = await reload.Set<NotificationSubscription>().FirstAsync(x =>
                x.UserId == userId &&
                x.ProviderKey == entity.ProviderKey &&
                x.ItemKey == entity.ItemKey, ct);
        }

        return subscription;
    }

    /// <summary>Updates the category switches on an existing subscription.</summary>
    public async Task<bool> UpdateOptionsAsync(
        long subscriptionId,
        int userId,
        NotificationSubscriptionOptions options,
        CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        NotificationSubscription? subscription = await db.Set<NotificationSubscription>()
            .FirstOrDefaultAsync(x => x.Id == subscriptionId && x.UserId == userId, ct);
        if (subscription is null)
            return false;

        subscription.NotifyStatus = options.Status;
        subscription.NotifyDownload = options.Download;
        subscription.NotifyNewContent = options.NewContent;
        subscription.NotifyProblems = options.Problems;
        subscription.NotifyLifecycle = options.Lifecycle;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UnsubscribeAsync(
        int userId,
        string providerKey,
        string itemKey,
        CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        int removed = await db.Set<NotificationSubscription>()
            .Where(x =>
                x.UserId == userId &&
                x.ProviderKey == providerKey &&
                x.ItemKey == itemKey)
            .ExecuteDeleteAsync(ct);
        return removed > 0;
    }

    /// <summary>Removes subscriptions created by a source, e.g. when a request is cancelled.</summary>
    public async Task<int> RemoveSourceAsync(string source, string sourceId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<NotificationSubscription>()
            .Where(x => x.Source == source && x.SourceId == sourceId)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Removes one user's subscription only when it came from the given source.</summary>
    public async Task<bool> RemoveIfSourceAsync(
        int userId,
        string providerKey,
        string itemKey,
        string source,
        CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        int removed = await db.Set<NotificationSubscription>()
            .Where(x =>
                x.UserId == userId &&
                x.ProviderKey == providerKey &&
                x.ItemKey == itemKey &&
                x.Source == source)
            .ExecuteDeleteAsync(ct);
        return removed > 0;
    }

    public async Task<NotificationSubscription?> GetAsync(
        int userId,
        string providerKey,
        string itemKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
            return null;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<NotificationSubscription>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.UserId == userId &&
                x.ProviderKey == providerKey &&
                x.ItemKey == itemKey, ct);
    }

    public async Task<bool> IsSubscribedAsync(
        int userId,
        string providerKey,
        string itemKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
            return false;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<NotificationSubscription>()
            .AsNoTracking()
            .AnyAsync(x =>
                x.UserId == userId &&
                x.ProviderKey == providerKey &&
                x.ItemKey == itemKey, ct);
    }

    public async Task<IReadOnlyList<NotificationSubscription>> GetForUserAsync(
        int userId,
        CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<NotificationSubscription>()
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Subscriptions whose entity and topic match. The ids are returned alongside the user ids so
    /// the caller can update <see cref="NotificationSubscription.LastNotifiedAt"/> without
    /// re-deriving the hierarchy match.
    /// </summary>
    public async Task<NotificationSubscriptionAudience> ResolveAudienceAsync(
        MediaEntityRef entity,
        string topic,
        CancellationToken ct = default)
    {
        if (!NotificationSubscriptionTopics.IsEntityTopic(topic))
            return NotificationSubscriptionAudience.Empty;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        List<NotificationSubscription> candidates = await db.Set<NotificationSubscription>()
            .AsNoTracking()
            .Where(x => x.ProviderKey == entity.ProviderKey)
            .ToListAsync(ct);

        HashSet<int> users = [];
        List<long> subscriptionIds = [];
        foreach (NotificationSubscription subscription in candidates)
        {
            if (!subscription.Matches(entity) || !subscription.Allows(topic))
                continue;

            users.Add(subscription.UserId);
            subscriptionIds.Add(subscription.Id);
        }

        return new NotificationSubscriptionAudience([.. users], subscriptionIds);
    }

    /// <summary>Records that matching subscriptions last produced an event, for the UI.</summary>
    public async Task TouchAsync(IReadOnlyList<long> subscriptionIds, CancellationToken ct = default)
    {
        if (subscriptionIds.Count == 0)
            return;

        DateTime now = time.GetUtcNow().UtcDateTime;
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.Set<NotificationSubscription>()
            .Where(x => subscriptionIds.Contains(x.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastNotifiedAt, now), ct);
    }
}

/// <summary>Resolved recipients of an entity notification, with the subscriptions that matched.</summary>
public sealed record NotificationSubscriptionAudience(
    IReadOnlyList<int> UserIds,
    IReadOnlyList<long> SubscriptionIds)
{
    public static NotificationSubscriptionAudience Empty { get; } = new([], []);
}
