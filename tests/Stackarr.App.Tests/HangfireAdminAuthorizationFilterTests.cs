using System.Security.Claims;
using Stackarr.App.Services;
using Stackarr.Auth.Abstractions;

namespace Stackarr.App.Tests;

/// <summary>
/// The Hangfire dashboard is an admin surface: anonymous and standard-user principals are
/// rejected even when the request looks local to the reverse proxy.
/// </summary>
public class HangfireAdminAuthorizationFilterTests
{
    static ClaimsPrincipal Principal(bool authenticated, string? role = null)
    {
        List<Claim> claims = [new(ClaimTypes.Name, "someone")];
        if (role is not null)
            claims.Add(new Claim(ClaimTypes.Role, role));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "stackarr.auth" : null));
    }

    [Test]
    public async Task IsAuthorized_RejectsAnonymous()
        => await Assert.That(HangfireAdminAuthorizationFilter.IsAuthorized(Principal(false))).IsFalse();

    [Test]
    public async Task IsAuthorized_RejectsStandardUser()
        => await Assert.That(
            HangfireAdminAuthorizationFilter.IsAuthorized(Principal(true, StackarrRoles.User))).IsFalse();

    [Test]
    public async Task IsAuthorized_AcceptsAdministrator()
        => await Assert.That(
            HangfireAdminAuthorizationFilter.IsAuthorized(Principal(true, StackarrRoles.Admin))).IsTrue();
}
