namespace Arrk.Auth.Abstractions;

/// <summary>
/// A service that mirrors Arrk accounts into an external account store (for example
/// Jellyfin). Implementations live in the corresponding service module, so Arrk never
/// depends on any specific player. When no provisioner is registered the account simply
/// stays local, which keeps the media-player modules optional.
/// </summary>
public interface IAccountProvisioner
{
    /// <summary>Stable provider key, e.g. <c>jellyfin</c>.</summary>
    string Provider { get; }

    /// <summary>
    /// Creates or updates the account in the provider and returns its external id. Must be
    /// idempotent: calling it twice for the same username updates rather than duplicates.
    /// A null <see cref="AccountProvisionRequest.Password"/> means "leave the password alone".
    /// </summary>
    Task<AccountProvisionResult> ProvisionAsync(AccountProvisionRequest request, CancellationToken ct = default);

    /// <summary>Removes the account from the provider.</summary>
    Task RemoveAsync(string externalId, CancellationToken ct = default);
}

/// <summary>Everything a provider needs to create or update an account.</summary>
/// <param name="Username">Desired username.</param>
/// <param name="DisplayName">Friendly name (may be ignored by providers).</param>
/// <param name="Password">Plaintext password, or null to leave the provider password unchanged.</param>
/// <param name="IsAdmin">Whether the account should be an administrator in the provider.</param>
/// <param name="IsActive">Whether the account should be enabled.</param>
/// <param name="ExternalId">
/// The provider's existing id for this account, when known. Providers must update this account
/// in place (including renames); when null they should look up by username then create.
/// </param>
public sealed record AccountProvisionRequest(
    string Username,
    string DisplayName,
    string? Password,
    bool IsAdmin,
    bool IsActive,
    string? ExternalId = null);

/// <summary>Outcome of provisioning an account into a provider.</summary>
/// <param name="ExternalId">The provider's identifier for the account.</param>
/// <param name="Created">True when the account did not previously exist in the provider.</param>
public sealed record AccountProvisionResult(string ExternalId, bool Created);
