using System.Security.Claims;

namespace Stackarr.Auth.Abstractions;

public enum ExternalSignInStatus
{
    SignedIn,
    Pending,
    Disabled,
    Unknown
}

/// <summary>A principal only exists when the identity may actually sign in.</summary>
public sealed record ExternalSignInResult(ClaimsPrincipal? Principal, ExternalSignInStatus Status, int? UserId);
