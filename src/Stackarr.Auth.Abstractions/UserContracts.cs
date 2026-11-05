using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Stackarr.Auth.Abstractions;

/// <summary>Read-only account directory used by modules that need to resolve a caller.</summary>
public interface IUserDirectory
{
    Task<bool> AnyAsync(CancellationToken ct = default);

    Task<IReadOnlyList<StackarrUser>> GetAllAsync(CancellationToken ct = default);

    Task<StackarrUser?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<StackarrUser?> GetByUsernameAsync(string username, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetRolesAsync(StackarrUser user);

    Task<IReadOnlyList<StackarrUser>> GetUsersInRoleAsync(string role, CancellationToken ct = default);

    Task<bool> IsInRoleAsync(StackarrUser user, string role);
}

/// <summary>Account mutations owned by the auth module's management UI.</summary>
public interface IUserAccountWriter
{
    Task<StackarrUser> CreateAsync(
        string username,
        string? password,
        UserRole role,
        string? displayName = null,
        CancellationToken ct = default);

    Task<bool> IsUsernameTakenAsync(string username, CancellationToken ct = default);

    Task<StackarrUser> UpdateProfileAsync(
        int id,
        string username,
        string? displayName,
        CancellationToken ct = default);

    Task ChangePasswordAsync(int id, string currentPassword, string newPassword, CancellationToken ct = default);

    Task ResetPasswordAsync(int id, string newPassword, CancellationToken ct = default);

    Task<StackarrUser> SetRoleAsync(int id, UserRole role, CancellationToken ct = default);

    Task<StackarrUser> SetActiveAsync(int id, bool isActive, CancellationToken ct = default);

    Task<StackarrUser> SetAvatarAsync(int id, byte[]? content, string? contentType, CancellationToken ct = default);

    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>External login/provider-link storage owned by the auth module.</summary>
public interface IExternalIdentityStore
{
    Task AddExternalLoginAsync(int userId, string provider, string externalId, string? displayName = null);

    Task<bool> TryAddExternalLoginAsync(
        int userId,
        string provider,
        string externalId,
        string? displayName = null,
        string? avatarUrl = null,
        CancellationToken ct = default);

    Task RemoveExternalLoginAsync(int userId, string provider, string externalId);

    Task<IReadOnlyList<UserLoginInfo>> GetExternalLoginsAsync(int userId);

    Task<bool> HasPasswordAsync(int userId);

    Task<UserProviderLink?> GetProviderLinkAsync(int userId, string provider, CancellationToken ct = default);

    Task ReconcileLinkedAccountsAsync(CancellationToken ct = default);
}

/// <summary>Password authentication used by the login endpoints.</summary>
public interface IUserAuthenticator
{
    Task<StackarrUser?> AuthenticateAsync(string username, string password, CancellationToken ct = default);
}

/// <summary>Session/security-stamp lookups used by the auth endpoints.</summary>
public interface IUserSessionReader
{
    Task<string> GetSecurityStampAsync(StackarrUser user);

    Task<StackarrSessionState?> GetSessionStateAsync(int id);
}

/// <summary>Resolves or links an upstream identity to a Stackarr account.</summary>
public interface IExternalIdentityResolver
{
    Task<ExternalSignInResult> SignInAsync(
        string provider,
        ClaimsPrincipal external,
        CancellationToken ct = default);

    Task<bool> LinkAsync(int userId, string provider, ClaimsPrincipal external, CancellationToken ct = default);
}

/// <summary>Live account state used to validate an existing cookie on every request.</summary>
public sealed record StackarrSessionState(
    StackarrUser User,
    IReadOnlyList<string> Roles,
    UserRole Role,
    bool IsActive,
    string SecurityStamp);

/// <summary>OIDC provider details a player module needs to configure its SSO integration.</summary>
public interface IOidcProviderInfo
{
    string ResolveIssuer();

    Task<string?> GetClientSecretAsync(CancellationToken ct = default);
}
