using Microsoft.Extensions.Logging;
using Stackarr.Core.Models;
using Stackarr.Core.Notifications;

namespace Stackarr.Core.Events;

/// <summary>
/// Decorates an <see cref="IEventStore"/> so that, after an event is durably recorded, any
/// notification attached to it is handed to the notification pipeline.
/// </summary>
/// <remarks>
/// This is the seam that keeps the three concerns distinct while avoiding double writes:
/// <list type="bullet">
/// <item><description>The <b>event bus</b> is for transient in-process reactions.</description></item>
/// <item><description>The <b>event store</b> is the durable audit log (this class's inner store).</description></item>
/// <item><description><b>Notifications</b> are the opted-in, delivered subset projected from records.</description></item>
/// </list>
/// Notification failures are logged and swallowed so a broken webhook can never prevent an
/// audit event from being persisted.
/// </remarks>
public sealed class NotifyingEventStore(
    IEventStore inner,
    IEnumerable<INotificationService> notifications,
    ILogger<NotifyingEventStore>? logger = null) : IEventStore
{
    readonly IReadOnlyList<INotificationService> _notifications = notifications.ToList();

    public Task RecordAsync(SystemEventRecord record, CancellationToken ct = default)
        => RecordAsync([record], ct);

    public async Task RecordAsync(IEnumerable<SystemEventRecord> records, CancellationToken ct = default)
    {
        List<SystemEventRecord> list = records as List<SystemEventRecord> ?? records.ToList();

        // Audit first: the event log must reflect reality even if notifications fail.
        await inner.RecordAsync(list, ct);

        if (_notifications.Count == 0)
            return;

        foreach (SystemEventRecord record in list)
        {
            if (record.Notification is null)
                continue;

            foreach (INotificationService service in _notifications)
            {
                try
                {
                    await service.PublishAsync(record.Notification, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to dispatch notification {Key}", record.Notification.Key);
                }
            }
        }
    }

    public Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventQuery query, CancellationToken ct = default)
        => inner.QueryAsync(query, ct);

    public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
        => inner.PurgeOlderThanAsync(cutoff, ct);

    public Task<IReadOnlyList<string>> GetEventTypesAsync(string? category = null, CancellationToken ct = default)
        => inner.GetEventTypesAsync(category, ct);
}
