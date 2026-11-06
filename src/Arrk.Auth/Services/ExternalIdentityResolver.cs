using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Auth.Services;

/// <summary>
/// Links an upstream identity to an Arrk account. Arrk remains the account owner: the
/// upstream identity only identifies the user; Arrk roles then drive authorization. In
/// <see cref="RegistrationMode.Approval"/> an unknown identity creates a pending placeholder
/// account but no session; in <see cref="RegistrationMode.AdminOnly"/> unknown identities are
/// refused outright.
/// </summary>
public sealed class ExternalIdentityResolver(
    UserService users,
    IOptionsMonitor<AuthConfig> config,
    ILogger<ExternalIdentityResolver> logger) : IExternalIdentityResolver
{
    public async Task<ExternalSignInResult> SignInAsync(
        string provider,
        ClaimsPrincipal external,
        CancellationToken ct = default)
    {
        string? subject = Subject(external);
        if (string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("{Provider} sign-in rejected: the token has no subject claim", provider);
            return new ExternalSignInResult(null, ExternalSignInStatus.Unknown, null);
        }

        RegistrationMode mode = config.CurrentValue.RegistrationMode;
        UserRole newUserRole = mode == RegistrationMode.Open ? UserRole.User : UserRole.Pending;

        string username = PreferredUsername(external, subject);
        ArrkUser? user = await users.GetOrCreateExternalUserAsync(
            provider,
            subject,
            username,
            username,
            Email(external),
            Picture(external),
            newUserRole,
            createIfMissing: mode != RegistrationMode.AdminOnly,
            ct);

        if (user is null)
            return new ExternalSignInResult(null, ExternalSignInStatus.Unknown, null);

        if (!user.IsActive)
        {
            logger.LogWarning("{Provider} sign-in rejected: account '{Username}' is disabled", provider, user.Username);
            return new ExternalSignInResult(null, ExternalSignInStatus.Disabled, user.Id);
        }

        if (user.Role == UserRole.Pending)
        {
            logger.LogInformation("{Provider} sign-in waiting for approval: account '{Username}'", provider, user.Username);
            return new ExternalSignInResult(null, ExternalSignInStatus.Pending, user.Id);
        }

        IReadOnlyList<string> roles = await users.GetRolesAsync(user);
        string securityStamp = await users.GetSecurityStampAsync(user);
        ClaimsPrincipal principal = new(
            ArrkClaims.Create(user, roles, CookieAuthenticationDefaults.AuthenticationScheme, provider, securityStamp));

        return new ExternalSignInResult(principal, ExternalSignInStatus.SignedIn, user.Id);
    }

    /// <summary>
    /// Attaches an upstream identity to an existing, signed-in account. Returns false when the
    /// identity is already linked elsewhere or the token is unusable.
    /// </summary>
    public async Task<bool> LinkAsync(int userId, string provider, ClaimsPrincipal external, CancellationToken ct = default)
    {
        string? subject = Subject(external);
        if (string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("{Provider} link rejected: the token has no subject claim", provider);
            return false;
        }

        bool linked = await users.TryAddExternalLoginAsync(
            userId, provider, subject, provider, Picture(external), ct);
        if (!linked)
            logger.LogWarning("{Provider} identity {Subject} could not be linked to user {UserId}", provider, subject, userId);

        return linked;
    }

    static string? Subject(ClaimsPrincipal external)
        => external.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? external.FindFirst("sub")?.Value;

    // Discord documents preferred_username as the user's handle (MyAnimeList publishes its name
    // the same way). A display-name claim is an acceptable fallback, but an email address is not a
    // Arrk username: skip any candidate that looks like one.
    static string PreferredUsername(ClaimsPrincipal external, string subject)
    {
        string?[] candidates =
        [
            external.FindFirst("preferred_username")?.Value,
            external.FindFirst("username")?.Value,
            external.FindFirst(ClaimTypes.Name)?.Value,
            external.FindFirst("global_name")?.Value,
            external.FindFirst("name")?.Value,
            external.FindFirst("nickname")?.Value
        ];

        foreach (string? candidate in candidates)
        {
            string? trimmed = candidate?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains('@'))
                return trimmed;
        }

        return $"user-{subject}";
    }

    static string? Picture(ClaimsPrincipal external)
        => external.FindFirst("picture")?.Value;

    static string? Email(ClaimsPrincipal external)
        => external.FindFirst(ClaimTypes.Email)?.Value
            ?? external.FindFirst("email")?.Value;
}
