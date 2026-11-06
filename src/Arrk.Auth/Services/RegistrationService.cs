using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Auth.Services;

/// <summary>
/// Self-service sign-up. Both modes create a real account immediately, so the username and any
/// linked Discord identity are reserved; the difference is the role. <see cref="RegistrationMode.Approval"/>
/// creates a placeholder <see cref="UserRole.Pending"/> account that every authorization policy
/// rejects until an administrator changes its role. <see cref="RegistrationMode.Open"/> creates a
/// normal user.
/// </summary>
public sealed class RegistrationService(
    UserService users,
    IOptionsMonitor<AuthConfig> config,
    ILogger<RegistrationService> logger)
{
    public RegistrationMode Mode => config.CurrentValue.RegistrationMode;

    /// <summary>
    /// Self-service local sign-up needs both its own switch and password login: a newly created
    /// local account could not authenticate otherwise. This is independent of
    /// <see cref="AuthConfig.AllowPasswordLogin"/> for existing accounts.
    /// </summary>
    public bool IsSelfServiceEnabled =>
        Mode != RegistrationMode.AdminOnly
        && config.CurrentValue.AllowLocalSignUp
        && config.CurrentValue.AllowPasswordLogin;

    /// <summary>
    /// Creates the account behind a sign-up. Pending accounts cannot sign in until approved.
    /// </summary>
    public async Task<RegistrationOutcome> RegisterAsync(
        string username,
        string password,
        CancellationToken ct = default)
    {
        if (!IsSelfServiceEnabled)
            throw new InvalidOperationException("Account sign-up is disabled.");

        UserService.ValidateUsername(username);
        UserService.ValidatePassword(password);

        string canonical = username.Trim();
        if (await users.IsUsernameTakenAsync(canonical, ct))
            throw new InvalidOperationException("That username is already taken.");

        UserRole role = Mode == RegistrationMode.Open ? UserRole.User : UserRole.Pending;
        ArrkUser user = await users.CreateAsync(canonical, password, role, canonical, ct);

        logger.LogInformation(
            "Self-service sign-up created {Role} account '{Username}'",
            role, canonical);

        return new RegistrationOutcome(RequiresApproval: role == UserRole.Pending, user);
    }
}

/// <summary>The result of a sign-up: the account always exists; its role decides whether it works.</summary>
public sealed record RegistrationOutcome(bool RequiresApproval, ArrkUser User);
