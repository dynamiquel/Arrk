using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Arrk.App.Services;

/// <summary>
/// Small, CSP-compatible hardening headers applied to every response. A full script policy is not
/// used because modules load their own assets and the agent UI uses inline bootstrapping; these
/// headers cover framing, MIME sniffing, referrer leakage (including stream tokens in URLs) and
/// device permissions, which are safe for the whole app.
/// </summary>
public static class SecurityHeaders
{
    public static void Apply(IHeaderDictionary headers)
    {
        // Assign rather than TryAdd: framework/static middleware can already have set a weaker
        // X-Frame-Options (SAMEORIGIN), and this app never needs to be framed.
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    }

    public static IApplicationBuilder UseArrkSecurityHeaders(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                Apply(context.Response.Headers);
                return Task.CompletedTask;
            });

            await next();
        });
}
