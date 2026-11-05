namespace Stackarr.Auth.Abstractions;

/// <summary>
/// Role names used on the ASP.NET Core cookie principal and by authorization policies.
/// Kept as constants so the App layer, the UI and tests all agree on the exact strings.
/// </summary>
public static class StackarrRoles
{
    public const string Admin = "Admin";
    public const string User = "User";

    /// <summary>
    /// A self-service sign-up that has not been approved yet. It exists as a real account so the
    /// identity is reserved (and Discord sign-up works), but no policy grants it any access.
    /// </summary>
    public const string Pending = "Pending";
}
