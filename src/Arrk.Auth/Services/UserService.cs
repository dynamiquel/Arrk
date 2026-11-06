using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Auth.Services;

/// <summary>
/// Arrk's account system, backed by ASP.NET Core Identity (credentials, roles, external logins)
/// with Arrk-owned profile fields on top. Accounts live in Arrk's database and are mirrored
/// into media players through <see cref="IAccountProvisioner"/>. Player provisioning is independent
/// of how a user signs in, so password clients such as Infuse keep working when Arrk itself is
/// OIDC-only.
/// </summary>
public sealed partial class UserService(
    UserManager<ArrkUser> users,
    RoleManager<IdentityRole<int>> roles,
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    IOptionsMonitor<AuthConfig> config,
    IEnumerable<IAccountProvisioner> provisioners,
    ExternalAvatarImporter avatarImporter,
    ILogger<UserService> logger)
    : IUserDirectory, IUserAccountWriter, IExternalIdentityStore, IUserAuthenticator, IUserSessionReader
{
    public const int MinPasswordLength = 4;
    public const int MaxPasswordLength = 256;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,62}[A-Za-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    // Reads

    public Task<bool> AnyAsync(CancellationToken ct = default) => users.Users.AnyAsync(ct);

    public async Task<IReadOnlyList<ArrkUser>> GetAllAsync(CancellationToken ct = default)
    {
        List<ArrkUser> all = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct);
        if (all.Count == 0)
            return all;

        Dictionary<int, List<UserProviderLink>> links = await LoadLinksAsync(ct);
        foreach (ArrkUser user in all)
        {
            user.Role = await GetRoleAsync(user);
            if (links.TryGetValue(user.Id, out List<UserProviderLink>? userLinks))
                user.ProviderLinks = userLinks;
        }

        return all;
    }

    public async Task<ArrkUser?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        ArrkUser? user = await users.FindByIdAsync(id.ToString());
        if (user is not null)
            await HydrateAsync(user, ct);
        return user;
    }

    public async Task<ArrkUser?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        ArrkUser? user = await users.FindByNameAsync(username);
        if (user is not null)
            await HydrateAsync(user, ct);
        return user;
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(ArrkUser user)
        => [.. await users.GetRolesAsync(user)];

    /// <summary>The Identity security stamp, copied into the auth cookie and checked on every request.</summary>
    public async Task<string> GetSecurityStampAsync(ArrkUser user)
        => await users.GetSecurityStampAsync(user) ?? "";

    /// <summary>
    /// The live account state the cookie validator needs. It deliberately does not hydrate provider
    /// links: this runs on every authenticated HTTP request, so it stays at two cheap lookups.
    /// </summary>
    public async Task<ArrkSessionState?> GetSessionStateAsync(int id)
    {
        ArrkUser? user = await users.FindByIdAsync(id.ToString());
        if (user is null)
            return null;

        IList<string> roles = await users.GetRolesAsync(user);
        UserRole role = roles.Contains(ArrkRoles.Admin)
            ? UserRole.Admin
            : roles.Contains(ArrkRoles.Pending)
                ? UserRole.Pending
                : UserRole.User;

        return new ArrkSessionState(
            user,
            [.. roles],
            role,
            user.IsActive,
            await users.GetSecurityStampAsync(user) ?? "");
    }

    /// <summary>All accounts holding a given role (e.g. Admin), used to address alerts.</summary>
    public async Task<IReadOnlyList<ArrkUser>> GetUsersInRoleAsync(string role, CancellationToken ct = default)
    {
        IList<ArrkUser> members = await users.GetUsersInRoleAsync(role);
        return [.. members];
    }

    public async Task<bool> IsInRoleAsync(ArrkUser user, string role)
        => await users.IsInRoleAsync(user, role);

    // Create / update

    /// <summary>
    /// Creates an account. The password is optional: OIDC-first deployments create accounts with
    /// no password, and a player password can be added later from the account page.
    /// </summary>
    public async Task<ArrkUser> CreateAsync(
        string username,
        string? password,
        UserRole role,
        string? displayName = null,
        CancellationToken ct = default)
    {
        ValidateUsername(username);
        if (password is not null)
            ValidatePassword(password);

        if (await users.FindByNameAsync(username.Trim()) is not null)
            throw new InvalidOperationException($"Username '{username.Trim()}' is already taken.");

        ArrkUser user = new()
        {
            UserName = username.Trim(),
            DisplayName = NormalizeDisplayName(displayName, username),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        IdentityResult result = password is null
            ? await users.CreateAsync(user)
            : await users.CreateAsync(user, password);
        EnsureSuccess(result, "create the account");

        await users.AddToRoleAsync(user, RoleName(role));
        user.Role = role;

        await SyncToProvidersAsync(user, password, ct);
        logger.LogInformation("Created {Role} account '{Username}'", role, user.Username);
        return user;
    }

    public async Task<bool> IsUsernameTakenAsync(string username, CancellationToken ct = default)
        => await users.FindByNameAsync(username.Trim()) is not null;

    public async Task<ArrkUser> UpdateProfileAsync(
        int id,
        string username,
        string? displayName,
        CancellationToken ct = default)
    {
        ValidateUsername(username);
        ArrkUser user = await RequireAsync(id);

        if (!string.Equals(user.UserName, username.Trim(), StringComparison.Ordinal))
        {
            IdentityResult renamed = await users.SetUserNameAsync(user, username.Trim());
            EnsureSuccess(renamed, "change the username");
        }

        user.DisplayName = NormalizeDisplayName(displayName, username);
        EnsureSuccess(await users.UpdateAsync(user), "update the profile");

        await SyncToProvidersAsync(user, password: null, ct);
        return user;
    }

    /// <summary>Changes the password used both for optional Arrk sign-in and for players.</summary>
    public async Task ChangePasswordAsync(int id, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        ValidatePassword(newPassword);
        ArrkUser user = await RequireAsync(id);

        if (!await users.HasPasswordAsync(user))
        {
            await SetPasswordInternalAsync(user, newPassword, ct);
            return;
        }

        IdentityResult result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "PasswordMismatch"))
                throw new InvalidOperationException("Current password is incorrect.");
            EnsureSuccess(result, "change the password");
        }

        await SyncToProvidersAsync(user, newPassword, ct);
    }

    /// <summary>Administrator-initiated password set/reset; does not require the current password.</summary>
    public async Task ResetPasswordAsync(int id, string newPassword, CancellationToken ct = default)
    {
        ArrkUser user = await RequireAsync(id);
        await SetPasswordInternalAsync(user, newPassword, ct);
    }

    async Task SetPasswordInternalAsync(ArrkUser user, string newPassword, CancellationToken ct)
    {
        ValidatePassword(newPassword);

        if (await users.HasPasswordAsync(user))
            EnsureSuccess(await users.RemovePasswordAsync(user), "clear the existing password");

        EnsureSuccess(await users.AddPasswordAsync(user, newPassword), "set the password");
        await SyncToProvidersAsync(user, newPassword, ct);
        logger.LogInformation("Password updated for '{Username}'", user.Username);
    }

    public async Task<ArrkUser> SetRoleAsync(int id, UserRole role, CancellationToken ct = default)
    {
        ArrkUser user = await RequireAsync(id);

        IList<string> current = await users.GetRolesAsync(user);
        if (current.Count > 0)
            EnsureSuccess(await users.RemoveFromRolesAsync(user, current), "remove the existing role");

        EnsureSuccess(await users.AddToRoleAsync(user, RoleName(role)), "assign the role");
        user.Role = role;

        await SyncToProvidersAsync(user, password: null, ct);
        return user;
    }

    public async Task<ArrkUser> SetActiveAsync(int id, bool isActive, CancellationToken ct = default)
    {
        ArrkUser user = await RequireAsync(id);
        user.IsActive = isActive;
        EnsureSuccess(await users.UpdateAsync(user), "update the account");
        await SyncToProvidersAsync(user, password: null, ct);
        return user;
    }

    public async Task<ArrkUser> SetAvatarAsync(int id, byte[]? content, string? contentType, CancellationToken ct = default)
    {
        ArrkUser user = await RequireAsync(id);
        user.Avatar = content;
        user.AvatarContentType = content is null ? null : contentType;
        EnsureSuccess(await users.UpdateAsync(user), "update the avatar");
        return user;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        ArrkUser? user = await users.FindByIdAsync(id.ToString());
        if (user is null)
            return;

        foreach (IAccountProvisioner provisioner in provisioners)
        {
            UserProviderLink? link = await GetProviderLinkAsync(id, provisioner.Provider, ct);
            if (link is null)
                continue;

            try
            {
                await provisioner.RemoveAsync(link.ExternalId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove '{Username}' from {Provider}", user.Username, provisioner.Provider);
            }
        }

        EnsureSuccess(await users.DeleteAsync(user), "delete the account");
        logger.LogInformation("Deleted account '{Username}'", user.Username);
    }

    // Authentication

    /// <summary>Verifies a username/password. Returns the account on success, or null.</summary>
    public async Task<ArrkUser?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return null;

        ArrkUser? user = await users.FindByNameAsync(username);
        if (user is null || !user.IsActive)
            return null;

        if (await users.IsLockedOutAsync(user))
            return null;

        bool passwordValid;
        try
        {
            passwordValid = await users.HasPasswordAsync(user) && await users.CheckPasswordAsync(user, password);
        }
        catch (FormatException)
        {
            // A password hash in a format Identity cannot parse (e.g. a legacy hash carried over
            // by a migration) must not surface as a 500, treat it as an invalid credential.
            logger.LogWarning("Stored password hash for '{Username}' is not in a supported format", user.Username);
            return null;
        }

        if (!passwordValid)
        {
            await users.AccessFailedAsync(user);
            return null;
        }

        await users.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = DateTime.UtcNow;
        await users.UpdateAsync(user);
        user.Role = await GetRoleAsync(user);
        return user;
    }

    // External logins (OIDC)

    /// <summary>
    /// Resolves the local account behind an external identity (OIDC), creating one on first login
    /// when allowed. New accounts get <paramref name="newUserRole"/>, <see cref="UserRole.Pending"/>
    /// in invite-only deployments, so Discord sign-up works without granting access.
    /// </summary>
    public async Task<ArrkUser?> GetOrCreateExternalUserAsync(
        string provider,
        string externalId,
        string preferredUsername,
        string? displayName = null,
        string? email = null,
        string? avatarUrl = null,
        UserRole newUserRole = UserRole.User,
        bool createIfMissing = true,
        CancellationToken ct = default)
    {
        ArrkUser? linked = await users.FindByLoginAsync(provider, externalId);
        if (linked is not null)
        {
            await ApplyExternalAvatarAsync(linked, avatarUrl, ct);
            linked.Role = await GetRoleAsync(linked);
            return linked;
        }

        if (!createIfMissing)
        {
            logger.LogInformation("External identity {Provider}:{ExternalId} is not linked to any account", provider, externalId);
            return null;
        }

        string username = await GenerateUniqueUsernameAsync(preferredUsername);
        AvatarImage? avatar = await avatarImporter.DownloadAsync(avatarUrl, ct);
        ArrkUser user = new()
        {
            UserName = username,
            DisplayName = NormalizeDisplayName(displayName, username),
            Email = string.IsNullOrWhiteSpace(email) ? null : email,
            EmailConfirmed = !string.IsNullOrWhiteSpace(email),
            Avatar = avatar?.Content,
            AvatarContentType = avatar?.ContentType,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        EnsureSuccess(await users.CreateAsync(user), "create the external account");
        await users.AddToRoleAsync(user, RoleName(newUserRole));
        user.Role = newUserRole;
        EnsureSuccess(await users.AddLoginAsync(user, new UserLoginInfo(provider, externalId, provider)), "link the external login");

        await SyncToProvidersAsync(user, password: null, ct);
        logger.LogInformation(
            "Created {Role} account '{Username}' from {Provider} identity {ExternalId}",
            newUserRole, username, provider, externalId);
        return user;
    }

    public async Task AddExternalLoginAsync(int userId, string provider, string externalId, string? displayName = null)
    {
        ArrkUser user = await RequireAsync(userId);
        EnsureSuccess(await users.AddLoginAsync(user, new UserLoginInfo(provider, externalId, displayName ?? provider)), "link the external login");
    }

    /// <summary>
    /// Links an upstream identity to an existing account. Returns false when that identity is
    /// already linked to a different account (or linking failed), without throwing.
    /// </summary>
    public async Task<bool> TryAddExternalLoginAsync(
        int userId,
        string provider,
        string externalId,
        string? displayName = null,
        string? avatarUrl = null,
        CancellationToken ct = default)
    {
        ArrkUser? owner = await users.FindByLoginAsync(provider, externalId);
        if (owner is not null)
        {
            if (owner.Id == userId)
                await ApplyExternalAvatarAsync(owner, avatarUrl, ct);
            return owner.Id == userId;
        }

        ArrkUser user = await RequireAsync(userId);
        IdentityResult result = await users.AddLoginAsync(user, new UserLoginInfo(provider, externalId, displayName ?? provider));
        if (!result.Succeeded)
        {
            logger.LogWarning("Could not link {Provider} identity {ExternalId} to '{Username}': {Errors}",
                provider, externalId, user.Username,
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return false;
        }

        await ApplyExternalAvatarAsync(user, avatarUrl, ct);
        return true;
    }

    public async Task RemoveExternalLoginAsync(int userId, string provider, string externalId)
    {
        ArrkUser user = await RequireAsync(userId);
        EnsureSuccess(await users.RemoveLoginAsync(user, provider, externalId), "unlink the external login");
    }

    public async Task<IReadOnlyList<UserLoginInfo>> GetExternalLoginsAsync(int userId)
    {
        ArrkUser user = await RequireAsync(userId);
        return [.. await users.GetLoginsAsync(user)];
    }

    public async Task<bool> HasPasswordAsync(int userId)
    {
        ArrkUser user = await RequireAsync(userId);
        return await users.HasPasswordAsync(user);
    }

    /// <summary>Re-applies username, role and enabled state for already-linked accounts.</summary>
    public async Task ReconcileLinkedAccountsAsync(CancellationToken ct = default)
    {
        if (!config.CurrentValue.Players.ProvisionAccounts)
            return;

        IReadOnlyList<ArrkUser> all = await GetAllAsync(ct);
        foreach (ArrkUser user in all.Where(u => u.ProviderLinks.Count > 0))
            await SyncToProvidersAsync(user, password: null, ct);
    }

    // Player provider links

    public async Task<UserProviderLink?> GetProviderLinkAsync(int userId, string provider, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.UserProviderLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Provider == provider, ct);
    }

    // Validation helpers

    public static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.", nameof(username));

        string trimmed = username.Trim();
        if (trimmed.Length < 3 || trimmed.Length > 64)
            throw new ArgumentException("Username must be between 3 and 64 characters.", nameof(username));

        if (!UsernamePattern().IsMatch(trimmed))
            throw new ArgumentException(
                "Username may only contain letters, numbers, dots, underscores and hyphens, and must start and end with a letter or number.",
                nameof(username));
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password is required.", nameof(password));

        if (password.Length < MinPasswordLength)
            throw new ArgumentException($"Password must be at least {MinPasswordLength} characters.", nameof(password));

        if (password.Length > MaxPasswordLength)
            throw new ArgumentException($"Password must be at most {MaxPasswordLength} characters.", nameof(password));
    }

    // Internals

    async Task<ArrkUser> RequireAsync(int id)
        => await users.FindByIdAsync(id.ToString()) ?? throw new InvalidOperationException($"User {id} does not exist.");

    async Task ApplyExternalAvatarAsync(ArrkUser user, string? avatarUrl, CancellationToken ct)
    {
        if (user.Avatar is { Length: > 0 } || string.IsNullOrWhiteSpace(avatarUrl))
            return;

        AvatarImage? avatar = await avatarImporter.DownloadAsync(avatarUrl, ct);
        if (avatar is null)
            return;

        user.Avatar = avatar.Content;
        user.AvatarContentType = avatar.ContentType;
        EnsureSuccess(await users.UpdateAsync(user), "update the avatar");
    }

    async Task HydrateAsync(ArrkUser user, CancellationToken ct)
    {
        user.Role = await GetRoleAsync(user);
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        user.ProviderLinks = await db.UserProviderLinks.AsNoTracking()
            .Where(l => l.UserId == user.Id).ToListAsync(ct);
    }

    public async Task<UserRole> GetRoleAsync(ArrkUser user)
    {
        if (await users.IsInRoleAsync(user, ArrkRoles.Admin))
            return UserRole.Admin;
        if (await users.IsInRoleAsync(user, ArrkRoles.Pending))
            return UserRole.Pending;
        return UserRole.User;
    }

    async Task<Dictionary<int, List<UserProviderLink>>> LoadLinksAsync(CancellationToken ct)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return (await db.UserProviderLinks.AsNoTracking().ToListAsync(ct))
            .GroupBy(l => l.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    async Task SyncToProvidersAsync(ArrkUser user, string? password, CancellationToken ct)
    {
        AuthConfig cfg = config.CurrentValue;
        if (!cfg.Players.ProvisionAccounts)
            return;

        string? passwordToSend = cfg.Players.ProvisionPasswords ? password : null;
        UserRole role = await GetRoleAsync(user);

        // Pending sign-ups are placeholders: they must not appear in media players until an
        // administrator approves them (which changes the role and triggers this sync again).
        if (role == UserRole.Pending)
            return;

        bool isAdmin = role == UserRole.Admin;
        bool isActive = user.IsActive;

        foreach (IAccountProvisioner provisioner in provisioners)
        {
            try
            {
                UserProviderLink? existing = await GetProviderLinkAsync(user.Id, provisioner.Provider, ct);
                AccountProvisionResult result = await provisioner.ProvisionAsync(
                    new AccountProvisionRequest(
                        user.Username,
                        user.Username,
                        passwordToSend,
                        isAdmin,
                        isActive,
                        existing?.ExternalId),
                    ct);

                if (existing?.ExternalId != result.ExternalId)
                    await SetProviderLinkAsync(user.Id, provisioner.Provider, result.ExternalId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not sync '{Username}' to {Provider}", user.Username, provisioner.Provider);
            }
        }
    }

    async Task SetProviderLinkAsync(int userId, string provider, string externalId, CancellationToken ct)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserProviderLink? link = await db.UserProviderLinks
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Provider == provider, ct);

        if (link is null)
        {
            db.UserProviderLinks.Add(new UserProviderLink
            {
                UserId = userId,
                Provider = provider,
                ExternalId = externalId,
                SyncedAt = DateTime.UtcNow
            });
        }
        else
        {
            link.ExternalId = externalId;
            link.SyncedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    async Task<string> GenerateUniqueUsernameAsync(string preferred)
    {
        string baseName = SanitizeUsername(preferred);
        if (string.IsNullOrEmpty(baseName))
            baseName = "user";
        if (baseName.Length > 60)
            baseName = baseName[..60];

        string candidate = baseName;
        int suffix = 1;
        while (await users.FindByNameAsync(candidate) is not null)
            candidate = $"{baseName}{++suffix}";

        return candidate;
    }

    static string SanitizeUsername(string value)
    {
        char[] cleaned = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')
            .ToArray();
        return new string(cleaned).Trim('-', '.', '_');
    }

    // Arrk identifies people by username; the display name is kept equal to it so legacy
    // profile data (e.g. an OIDC email) never surfaces in the UI.
    static string NormalizeDisplayName(string? displayName, string username)
        => username.Trim();

    public static string RoleName(UserRole role) =>
        role switch
        {
            UserRole.Admin => ArrkRoles.Admin,
            UserRole.Pending => ArrkRoles.Pending,
            _ => ArrkRoles.User
        };

    static void EnsureSuccess(IdentityResult result, string action)
    {
        if (result.Succeeded)
            return;

        string errors = string.Join("; ", result.Errors.Select(e => e.Description));
        throw new InvalidOperationException($"Could not {action}: {errors}");
    }

}
