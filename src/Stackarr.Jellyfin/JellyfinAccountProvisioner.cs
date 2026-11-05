using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Auth.Abstractions;

namespace Stackarr.Jellyfin;

/// <summary>
/// Mirrors Stackarr accounts into Jellyfin using Jellyfin's own user API. This is what makes a
/// single set of credentials work across the Stackarr UI and every Jellyfin client (web, Apple
/// TV, Xbox and Infuse), because Jellyfin still authenticates with a username and password;
/// Stackarr simply creates and maintains those accounts on the server's behalf.
/// </summary>
public sealed class JellyfinAccountProvisioner(
    JellyfinClient client,
    IOptionsMonitor<JellyfinConfig> config,
    ILogger<JellyfinAccountProvisioner> logger) : IAccountProvisioner
{
    public string Provider => "jellyfin";

    public async Task<AccountProvisionResult> ProvisionAsync(AccountProvisionRequest request, CancellationToken ct = default)
    {
        JellyfinConfig current = config.CurrentValue;
        if (!current.Enabled || !current.ManageUsers)
            return new AccountProvisionResult("", Created: false);

        // Prefer the id Stackarr already knows so a rename updates the existing account rather
        // than creating a second one. Fall back to matching by name.
        JellyfinUser? user = null;
        if (!string.IsNullOrEmpty(request.ExternalId))
        {
            try
            {
                user = await client.GetUserByIdAsync(request.ExternalId, ct);
            }
            catch (HttpRequestException)
            {
                user = null;
            }
        }

        user ??= (await client.GetUsersAsync(ct)).FirstOrDefault(u =>
            u.Name.Equals(request.Username, StringComparison.OrdinalIgnoreCase));

        bool created = false;
        if (user is null)
        {
            user = await client.CreateUserAsync(request.Username, request.Password ?? string.Empty, ct);
            created = true;
            logger.LogInformation("Created Jellyfin user '{Username}'", request.Username);
        }
        else if (!user.Name.Equals(request.Username, StringComparison.Ordinal))
        {
            user = await client.RenameUserAsync(user.Id, request.Username, ct);
            logger.LogInformation("Renamed Jellyfin user {Id} to '{Username}'", user.Id, request.Username);
        }

        // Never demote, disable or password-change the Jellyfin administrator Stackarr itself
        // signs in as, or Stackarr would lose the ability to manage the server. (Jellyfin also
        // requires the *current* password to change your own, which Stackarr cannot supply.)
        bool isStackarrAdmin = user.Name.Equals(client.Username, StringComparison.OrdinalIgnoreCase);

        if (request.Password is not null)
        {
            if (isStackarrAdmin)
            {
                logger.LogDebug("Skipping password update for the Jellyfin administrator '{Username}'", user.Name);
            }
            else
            {
                await client.SetUserPasswordAsync(user.Id, request.Password, ct);
            }
        }

        JellyfinUserPolicy policy = user.Policy ?? new JellyfinUserPolicy();
        policy.IsAdministrator = request.IsAdmin || isStackarrAdmin;
        policy.IsDisabled = !request.IsActive && !isStackarrAdmin;

        if (isStackarrAdmin && (!request.IsAdmin || !request.IsActive))
        {
            logger.LogWarning(
                "Refusing to demote/disable the Jellyfin administrator '{Username}' used by Stackarr",
                user.Name);
        }

        await client.UpdateUserPolicyAsync(user.Id, policy, ct);
        return new AccountProvisionResult(user.Id, created);
    }

    public async Task RemoveAsync(string externalId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(externalId) || !config.CurrentValue.Enabled)
            return;

        await client.DeleteUserAsync(externalId, ct);
        logger.LogInformation("Deleted Jellyfin user {Id}", externalId);
    }
}
