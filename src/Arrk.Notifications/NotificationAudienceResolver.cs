using Arrk.Auth.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Arrk.Notifications;

/// <summary>Resolves selectors once, independently of transport capabilities and availability.</summary>
public sealed class NotificationAudienceResolver(
    IServiceScopeFactory scopeFactory,
    NotificationSubscriptionService subscriptions)
{
    public async Task<ResolvedNotificationAudience> ResolveAsync(NotificationMessage message, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IUserDirectory users = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
        IReadOnlyList<ArrkUser> accounts = await users.GetAllAsync(ct);
        HashSet<int> active = accounts.Where(u => u.IsActive && u.Role != UserRole.Pending).Select(u => u.Id).ToHashSet();
        HashSet<int> direct = message.Audience.UserIds.ToHashSet();
        Dictionary<string, IReadOnlyList<int>> roleUsers = new(StringComparer.OrdinalIgnoreCase);

        foreach (string role in message.Audience.Roles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<ArrkUser> members = await users.GetUsersInRoleAsync(role, ct);
            roleUsers[role] = [.. members.Select(u => u.Id).Where(active.Contains).Distinct()];
            direct.UnionWith(roleUsers[role]);
        }

        NotificationSubscriptionAudience followers = NotificationSubscriptionAudience.Empty;
        if (message.Audience.FollowersOf is { } entity)
        {
            followers = await subscriptions.ResolveAudienceAsync(entity, message.Key, ct);
            direct.UnionWith(followers.UserIds);
        }

        direct.IntersectWith(active);
        HashSet<int> inbox = message.Audience.Global ? active : [.. direct];
        inbox.UnionWith(direct);
        return new(message.Audience.Global, [.. inbox.Order()], [.. direct.Order()], roleUsers, followers.SubscriptionIds);
    }
}

public sealed record ResolvedNotificationAudience(
    bool Global,
    IReadOnlyList<int> InboxUserIds,
    IReadOnlyList<int> DirectUserIds,
    IReadOnlyDictionary<string, IReadOnlyList<int>> RoleUserIds,
    IReadOnlyList<long> SubscriptionIds);
