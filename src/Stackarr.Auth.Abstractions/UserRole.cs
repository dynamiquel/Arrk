namespace Stackarr.Auth.Abstractions;

/// <summary>
/// The account roles Stackarr understands. Admins can manage the stack (and are provisioned as
/// administrators in media players); users get a restricted, read-only experience and can only
/// request/add content. A pending account is a placeholder for a self-service sign-up: it can
/// authenticate but every authorization policy rejects it until an admin changes its role.
/// </summary>
public enum UserRole
{
    /// <summary>A standard user: read-only access, can add/monitor content, cannot change state.</summary>
    User = 0,

    /// <summary>Full administrative access to Stackarr and its services.</summary>
    Admin = 1,

    /// <summary>A self-service sign-up awaiting approval; no access to anything.</summary>
    Pending = 2
}
