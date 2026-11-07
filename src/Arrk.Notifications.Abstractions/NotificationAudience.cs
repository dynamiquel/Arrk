namespace Arrk.Notifications;

/// <summary>
/// Additive audience selectors. Roles, users and followers resolve to a deduplicated set of
/// direct recipients. Global adds everyone's inbox and shared endpoints without suppressing DMs.
/// Named roles can also be routed to explicitly configured shared endpoints by each client.
/// An empty audience deliberately addresses nobody.
/// </summary>
public sealed record NotificationAudience
{
    public bool Global { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<int> UserIds { get; init; } = [];
    public MediaEntityRef? FollowersOf { get; init; }
}

/// <summary>A resolved destination: global, a named role, or one local account.</summary>
public sealed record NotificationRecipient(int? UserId, string? Role = null)
{
    public static NotificationRecipient Global { get; } = new((int?)null);
    public bool IsGlobal => UserId is null && Role is null;
}
