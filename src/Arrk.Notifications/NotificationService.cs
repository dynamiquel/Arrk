using Arrk.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Notifications;

/// <summary>
/// Applies topic policy, persists one inbox entry per account, and sends to resolved destinations.
/// No transport-specific configuration, user identity, or audience precedence lives here.
/// </summary>
public sealed class NotificationService(
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    IOptionsMonitor<NotificationConfig> config,
    NotificationEventRegistry registry,
    NotificationAudienceResolver audiences,
    NotificationSubscriptionService subscriptions,
    IEnumerable<INotificationClient> clients,
    TimeProvider time,
    ILogger<NotificationService> logger) : INotificationService
{
    readonly IReadOnlyList<INotificationClient> _clients = clients.ToList();

    public async Task<Notification?> PublishAsync(NotificationMessage message, CancellationToken ct = default)
    {
        if (!registry.IsEnabled(config.CurrentValue, message.Key))
            return null;

        ResolvedNotificationAudience audience = await audiences.ResolveAsync(message, ct);
        List<Notification> entries = audience.InboxUserIds.Select(userId => new Notification
        {
            UserId = userId,
            Key = message.Key,
            Category = registry.GroupOf(message.Key),
            Title = message.Title,
            Body = message.Body,
            Severity = message.Severity.ToString(),
            Url = message.Url,
            Source = message.Source,
            SourceName = message.SourceName,
            Data = message.Data,
            CreatedAt = message.Timestamp
        }).ToList();

        // Inbox persistence does not depend on a client being enabled or accepting delivery.
        await using (ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            db.Notifications.AddRange(entries);
            await db.SaveChangesAsync(ct);
        }

        List<NotificationRecipient> recipients = [];
        if (audience.Global)
            recipients.Add(NotificationRecipient.Global);
        recipients.AddRange(audience.RoleUserIds.Keys.Select(role => new NotificationRecipient(null, role)));
        recipients.AddRange(audience.DirectUserIds.Select(id => new NotificationRecipient(id)));

        try
        {
            foreach (NotificationRecipient recipient in recipients)
            {
                Notification[] relevant = entries.Where(n => recipient.IsGlobal || n.UserId == recipient.UserId
                    || (recipient.Role is { } role && audience.RoleUserIds[role].Contains(n.UserId!.Value))).ToArray();
                foreach (INotificationClient client in _clients)
                {
                    ct.ThrowIfCancellationRequested();
                    NotificationDeliveryResult result;
                    try
                    {
                        result = await client.SendAsync(message, recipient, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Notification client {Client} failed for {Topic}", client.Name, message.Key);
                        result = NotificationDeliveryResult.Fail(ex.Message);
                    }

                    foreach (Notification entry in relevant)
                    {
                        if (!result.Success)
                            entry.Error = string.Join("; ", new[] { entry.Error, $"{client.Name}: {result.Error}" }.Where(e => e is not null));
                        else if (!result.Skipped)
                        {
                            entry.Delivered = true;
                            entry.Channels = string.Join(",", (entry.Channels?.Split(',') ?? []).Append(client.Name).Distinct());
                        }
                    }
                    logger.LogInformation("Notification {Topic} to {Recipient} via {Client}: {Outcome}",
                        message.Key, recipient, client.Name, result);
                }
            }
        }
        finally
        {
            // Preserve completed sends even when a later client is cancelled. Update only delivery
            // fields so an inbox read performed during delivery is never overwritten.
            await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            foreach (Notification entry in entries)
            {
                db.Attach(entry);
                db.Entry(entry).Property(n => n.Delivered).IsModified = true;
                db.Entry(entry).Property(n => n.Channels).IsModified = true;
                db.Entry(entry).Property(n => n.Error).IsModified = true;
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await subscriptions.TouchAsync(audience.SubscriptionIds, ct);
        return entries.FirstOrDefault();
    }

    public Task<Notification?> PublishToUserAsync(int userId, NotificationMessage message, CancellationToken ct = default) =>
        PublishAsync(message with { Audience = new() { UserIds = [userId] } }, ct);

    public async Task<IReadOnlyList<Notification>> QueryAsync(NotificationQuery query, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        IQueryable<Notification> q = db.Notifications.AsNoTracking().Where(n => n.UserId != null);
        if (query.UserId is { } userId)
            q = q.Where(n => n.UserId == userId);
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
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
            q = q.Where(n => n.Title.Contains(search) || n.Body.Contains(search)
                || (n.SourceName != null && n.SourceName.Contains(search)));
        }
        if (query.Since is { } since)
            q = q.Where(n => n.CreatedAt >= since);
        int limit = query.Limit <= 0 ? 200 : Math.Min(query.Limit, 2000);
        return await q.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<Notification?> MarkReadAsync(long id, int userId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        Notification? notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);
        if (notification is null)
            return null;
        notification.ReadAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return notification;
    }

    public async Task<int> MarkAllReadAsync(int userId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        DateTime now = time.GetUtcNow().UtcDateTime;
        return await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
    }

    public async Task<int> UnreadCountAsync(int userId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Notifications.Where(n => n.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
