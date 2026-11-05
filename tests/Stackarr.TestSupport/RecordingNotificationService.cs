using Stackarr.Core.Models;
using Stackarr.Core.Notifications;

namespace Stackarr.TestSupport;

/// <summary>
/// In-memory <see cref="INotificationService"/> that records every publish for assertions,
/// including the audience (broadcast or a single account).
/// </summary>
public sealed class RecordingNotificationService : INotificationService
{
    public List<(NotificationMessage Message, int? UserId)> Published { get; } = [];

    public IReadOnlyList<NotificationMessage> Messages => [.. Published.Select(p => p.Message)];

    public Task<Notification?> PublishAsync(NotificationMessage message, CancellationToken ct = default)
    {
        Published.Add((message, message.AudienceUserId));
        return Task.FromResult<Notification?>(new Notification { Key = message.Key, Title = message.Title });
    }

    public Task<Notification?> PublishToUserAsync(int userId, NotificationMessage message, CancellationToken ct = default)
    {
        Published.Add((message, userId));
        return Task.FromResult<Notification?>(new Notification { Key = message.Key, Title = message.Title, UserId = userId });
    }

    public Task<IReadOnlyList<Notification>> QueryAsync(NotificationQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Notification>>([]);

    public Task<Notification?> MarkReadAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<Notification?>(null);

    public Task<int> MarkAllReadAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task<int> MarkAllReadAsync(int? userId, CancellationToken ct = default) => Task.FromResult(0);

    public Task<int> UnreadCountAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task<int> UnreadCountAsync(int? userId, CancellationToken ct = default) => Task.FromResult(0);

    public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
}
