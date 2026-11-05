using System.Security.Claims;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Stackarr.Auth.Abstractions;

namespace Stackarr.App.Services;

/// <summary>
/// Restricts the Hangfire dashboard to signed-in Stackarr administrators. Hangfire's default
/// local-request filter is not enough behind a reverse proxy, where every request can look local.
/// </summary>
public sealed class HangfireAdminAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        HttpContext? http = context.GetHttpContext();
        return http is not null && IsAuthorized(http.User);
    }

    /// <summary>Only a signed-in administrator may view the scheduler dashboard.</summary>
    public static bool IsAuthorized(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true
            && user.IsInRole(StackarrRoles.Admin);
}
