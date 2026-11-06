using System.Security.Claims;
namespace Arrk.Auth.Abstractions;

/// <summary>
/// Builds the claims principal stored in the Arrk authentication cookie from a local
/// account plus its Identity roles.
/// </summary>
public static class ArrkClaims
{
    public const string UserIdClaim = "arrk:user_id";
    public const string DisplayNameClaim = "arrk:display_name";
    public const string ProviderClaim = "arrk:provider";

    /// <summary>Identity security stamp copied into the cookie so a password reset or change can
    /// revoke every existing session. The cookie validator rejects a stale stamp.</summary>
    public const string SecurityStampClaim = "arrk:security_stamp";

    public static ClaimsIdentity Create(
        ArrkUser user,
        IEnumerable<string> roles,
        string authenticationType,
        string provider = "local",
        string? securityStamp = null)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(UserIdClaim, user.Id.ToString()),
            new(DisplayNameClaim, user.Username),
            new(ProviderClaim, provider)
        ];

        if (!string.IsNullOrEmpty(securityStamp))
            claims.Add(new Claim(SecurityStampClaim, securityStamp));

        foreach (string role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        return new ClaimsIdentity(claims, authenticationType);
    }
}
