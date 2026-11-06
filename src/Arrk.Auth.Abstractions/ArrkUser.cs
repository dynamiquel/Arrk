using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace Arrk.Auth.Abstractions;

/// <summary>
/// An Arrk account. Backed by ASP.NET Core Identity for credentials (password hashing,
/// lockout, external logins, roles); Arrk adds the profile and provisioning fields it owns.
/// </summary>
public class ArrkUser : IdentityUser<int>
{
    /// <summary>Friendly name shown in the UI; falls back to the username when blank.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>When false the account cannot sign in and is disabled in media players.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Raw profile picture bytes (small) stored in Arrk's own database.</summary>
    public byte[]? Avatar { get; set; }

    public string? AvatarContentType { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Links to this account in external *media players* (Jellyfin, ...), distinct from
    /// Identity's external logins (AspNetUserLogins) used for OIDC sign-in.</summary>
    public ICollection<UserProviderLink> ProviderLinks { get; set; } = [];

    /// <summary>Convenience alias over <see cref="IdentityUser{TKey}.UserName"/>.</summary>
    [NotMapped]
    public string Username
    {
        get => UserName ?? "";
        set => UserName = value;
    }

    /// <summary>
    /// Transient role populated by the account service for UI convenience. Not persisted;
    /// the authoritative role lives in Identity roles.
    /// </summary>
    [NotMapped]
    public UserRole Role { get; set; } = UserRole.User;
}

/// <summary>Maps an Arrk account to its identity in an external media player.</summary>
public class UserProviderLink
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public ArrkUser? User { get; set; }

    /// <summary>Provider key, e.g. <c>jellyfin</c>.</summary>
    public string Provider { get; set; } = "";

    /// <summary>The identifier the provider assigned to this account.</summary>
    public string ExternalId { get; set; } = "";

    public DateTime SyncedAt { get; set; }
}
