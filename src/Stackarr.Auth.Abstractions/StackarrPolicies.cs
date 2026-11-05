namespace Stackarr.Auth.Abstractions;

/// <summary>
/// Authorization policy names. Policies live in Core (rather than the App) so modules and
/// tests can reference them without taking a dependency on the web host.
/// </summary>
public static class StackarrPolicies
{
    /// <summary>Requires an authenticated user.</summary>
    public const string Authenticated = "Stackarr.Authenticated";

    /// <summary>Requires the <see cref="StackarrRoles.Admin"/> role.</summary>
    public const string AdminOnly = "Stackarr.AdminOnly";

    /// <summary>Per-IP limit applied to anonymous credential and OAuth-start endpoints.</summary>
    public const string AuthRateLimit = "Stackarr.AuthRateLimit";
}
