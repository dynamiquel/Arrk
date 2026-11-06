namespace Arrk.Auth.Abstractions;

/// <summary>
/// Authorization policy names. Policies live in Core (rather than the App) so modules and
/// tests can reference them without taking a dependency on the web host.
/// </summary>
public static class ArrkPolicies
{
    /// <summary>Requires an authenticated user.</summary>
    public const string Authenticated = "Arrk.Authenticated";

    /// <summary>Requires the <see cref="ArrkRoles.Admin"/> role.</summary>
    public const string AdminOnly = "Arrk.AdminOnly";

    /// <summary>Per-IP limit applied to anonymous credential and OAuth-start endpoints.</summary>
    public const string AuthRateLimit = "Arrk.AuthRateLimit";
}
