using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;

namespace Stackarr.Core.Notifications;

/// <summary>
/// Default notification inbox + dispatcher. Applies the user's notification preferences, records
/// every accepted alert in the in-app inbox, then fans it out to all configured channels.
/// </summary>
/// <remarks>
/// This is core infrastructure: it knows nothing about Discord or any transport, only the
/// <see cref="INotificationChannel"/> contract. Delivery transports are contributed by modules.
/// Broadcast alerts use the global channels; subscriber/user alerts create one inbox row per
/// recipient and use each channel's direct-delivery path, so they never leak into the global
/// webhook.
/// </remarks>
public sealed class NotificationService(
    IDbContextFactory<StackarrDbContext> dbContextFactory,
    IOptionsMonitor<NotificationConfig> config,
    NotificationEventRegistry registry,
    NotificationSubscriptionService subscriptions,
    IEnumerable<INotificationChannel> channels,
    TimeProvider time,
    ILogger<NotificationService> logger) : INotificationService
{
    readonly IReadOnlyList<INotificationChannel> _channels = channels.ToList();

    public async Task<Notification?> PublishAsync(NotificationMessage message, CancellationToken ct = default)
    {
        if (!registry.IsEnabled(config.CurrentValue, message.Key))
        {
            logger.LogTrace("Notification {Topic} suppressed by configuration", message.Key);
            return null;
        }

        (IReadOnlyList<int?> targets, IReadOnlyList<long> subscriptionIds) = await ResolveTargetsAsync(message, ct);
        if (targets.Count == 0)
        {
            logger.LogDebug("Notification {Topic} had no recipients", message.Key);
            return null;
        }

        Notification? first = null;
        foreach (int? userId in targets)
        {
            Notification notification = await PersistAsync(userId, message, ct);
            first ??= notification;
            await DeliverAsync(notification, userId, message, ct);
        }

        await subscriptions.TouchAsync(subscriptionIds, ct);

        return first;
    }

    public Task<Notification?> PublishToUserAsync(int userId, NotificationMessage message, CancellationToken ct = default) =>
        PublishAsync(message with
        {
            Audience = NotificationAudience.Users,
            AudienceUserId = userId,
            AudienceUserIds = [userId]
        }, ct);

    async Task<(IReadOnlyList<int?> Targets, IReadOnlyList<long> SubscriptionIds)> ResolveTargetsAsync(
        NotificationMessage message,
        CancellationToken ct)
    {
        // Legacy single-recipient field wins over the audience enum.
        if (message.AudienceUserId is { } single)
            return ([single], []);

        switch (message.Audience)
        {
            case NotificationAudience.Users:
                return (message.AudienceUserIds.Distinct().Select(id => (int?)id).ToList(), []);

            case NotificationAudience.Subscribers:
                if (message.Entity is null)
                {
                    logger.LogWarning(
                        "Notification {Topic} asked for subscribers but carried no entity; dropping it",
                        message.Key);
                    return ([], []);
                }

                NotificationSubscriptionAudience audience = await subscriptions.ResolveAudienceAsync(
                    message.Entity, message.Key, ct);
                return (audience.UserIds.Select(id => (int?)id).ToList(), audience.SubscriptionIds);

            default:
                return ([null], []);
        }
    }

    async Task<Notification> PersistAsync(int? userId, NotificationMessage message, CancellationToken ct)
    {
        Notification entity = new()
        {
            Key = message.Key,
            Category = registry.GroupOf(message.Key),
            Title = message.Title,
            Body = message.Body,
            Severity = message.Severity.ToString(),
            Url = message.Url,
            Source = message.Source,
            SourceName = message.SourceName,
            Data = message.Data,
            UserId = userId,
            CreatedAt = message.Timestamp
        };

        // Persist before delivery so the inbox always reflects an accepted alert even if a
        // channel is down. Delivery outcome is written back afterwards.
        await using (StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            db.Notifications.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        return entity;
    }

    async Task DeliverAsync(
        Notification entity,
        int? userId,
        NotificationMessage message,
        CancellationToken ct)
    {
        List<string> delivered = [];
        List<string> errors = [];

        foreach (INotificationChannel channel in _channels)
        {
            if (!channel.IsConfigured)
                continue;

            if (userId is not null && !channel.SupportsDirectDelivery)
                continue;

            try
            {
                NotificationDeliveryResult result = userId is null
                    ? await channel.SendAsync(message, ct)
                    : await channel.SendToUserAsync(userId.Value, message, ct);

                if (result.Success)
                {
                    if (!result.Skipped)
                        delivered.Add(channel.Name);
                    else
                        logger.LogTrace(
                            "Notification {Topic} skipped by {Channel} filter", message.Key, channel.Name);
                }
                else
                {
                    errors.Add($"{channel.Name}: {result.Error}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification channel {Channel} threw for {Topic}", channel.Name, message.Key);
                errors.Add($"{channel.Name}: {ex.Message}");
            }
        }

        entity.Delivered = delivered.Count > 0;
        entity.Channels = delivered.Count > 0 ? string.Join(",", delivered) : null;
        entity.Error = errors.Count > 0 ? string.Join("; ", errors) : null;

        await using (StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            db.Notifications.Update(entity);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Notification {Topic} recorded for {Audience} (delivered: {Delivered}; channels: {Channels})",
            message.Key,
            userId is null ? "everyone" : $"user {userId}",
            entity.Delivered,
            entity.Channels ?? "none");
    }

    public async Task<IReadOnlyList<Notification>> QueryAsync(NotificationQuery query, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        IQueryable<Notification> q = db.Notifications.AsNoTracking();

        if (query.UserId is { } queryUserId)
        {
            q = query.IncludeBroadcast
                ? q.Where(n => n.UserId == queryUserId || n.UserId == null)
                : q.Where(n => n.UserId == queryUserId);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            // Treat Category as a topic prefix so callers can filter by domain or group.
            string category = query.Category;
            q = q.Where(n => n.Key == category || n.Key.StartsWith(category + "."));
        }

        if (!string.IsNullOrWhiteSpace(query.Key))
            q = q.Where(n => n.Key == query.Key);

        if (query.UnreadOnly)
            q = q.Where(n => n.ReadAt == null);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string search = query.Search;
            q = q.Where(n =>
                n.Title.Contains(search) ||
                n.Body.Contains(search) ||
                (n.SourceName != null && n.SourceName.Contains(search)));
        }

        if (query.Since is { } since)
            q = q.Where(n => n.CreatedAt >= since);

        int limit = query.Limit <= 0 ? 200 : Math.Min(query.Limit, 2000);

        return await q.OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<Notification?> MarkReadAsync(long id, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        Notification? notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (notification is null)
            return null;

        notification.ReadAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return notification;
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Notifications
            .Where(n => n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
    }

    public async Task<int> MarkAllReadAsync(int? userId, CancellationToken ct = default)
    {
        if (userId is null)
            return await MarkAllReadAsync(ct);

        DateTime now = time.GetUtcNow().UtcDateTime;
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Notifications
            .Where(n => n.ReadAt == null && (n.UserId == userId || n.UserId == null))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
    }

    public async Task<int> UnreadCountAsync(CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Notifications.CountAsync(n => n.ReadAt == null, ct);
    }

    public async Task<int> UnreadCountAsync(int? userId, CancellationToken ct = default)
    {
        if (userId is null)
            return await UnreadCountAsync(ct);

        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Notifications.CountAsync(n => n.ReadAt == null && (n.UserId == userId || n.UserId == null), ct);
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Notifications.Where(n => n.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
