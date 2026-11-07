using Arrk.Auth.Abstractions;

namespace Arrk.Notifications;

/// <summary>
/// Additive audience selectors. Roles, users and followers resolve to a deduplicated set of
/// inbox recipients. The User role includes approved accounts (admins also hold User), without
/// bulk DMs; narrower roles, explicit users and followers additionally select direct recipients.
/// An empty audience deliberately addresses nobody.
/// </summary>
public sealed record NotificationAudience
{
    public static NotificationAudience Everyone { get; } = new() { Roles = [ArrkRoles.User] };

    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<int> UserIds { get; init; } = [];
    public MediaEntityRef? FollowersOf { get; init; }
}

/// <summary>A resolved destination: a named role or one local account.</summary>
public sealed record NotificationRecipient(int? UserId, string? Role = null)
{
    public static NotificationRecipient Everyone { get; } = new(null, ArrkRoles.User);
}
