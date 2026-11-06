using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Auth.Abstractions;
using Arrk.Mal.Clients;
using Arrk.Mal.Models;
using Arrk.Mal.Services;

namespace Arrk.Mal.Auth;

/// <summary>
/// OAuth start/callback endpoints. The same callback serves account linking and sign-in: the
/// protected state decides which, and binds a link to the Arrk user that started it.
/// </summary>
public static class MalLinkEndpoints
{
    public static void MapMalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/mal-signin/start", StartAsync)
            .AllowAnonymous()
            .RequireRateLimiting(ArrkPolicies.AuthRateLimit);
        endpoints.MapGet("/mal-signin", CallbackAsync)
            .AllowAnonymous()
            .RequireRateLimiting(ArrkPolicies.AuthRateLimit);
    }

    static IResult StartAsync(
        HttpContext http,
        IOptionsMonitor<MalConfig> config,
        MalOAuthService oauth)
    {
        if (!config.CurrentValue.IsConfigured)
            return Results.Redirect("/mal?error=config");

        string mode = http.Request.Query["mode"].ToString() is { Length: > 0 } requested
            ? requested
            : "login";
        string returnUrl = Normalize(http.Request.Query["returnUrl"]);

        int? userId = null;
        if (string.Equals(mode, "link", StringComparison.Ordinal))
        {
            userId = int.TryParse(http.User.FindFirst(ArrkClaims.UserIdClaim)?.Value, out int id)
                ? id
                : null;
            if (userId is null)
                return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString("/account")}");
        }

        MalOAuthAuthorizeRequest request = oauth.CreateAuthorizeRequest(http.Request, http.Response, mode, userId, returnUrl);
        return Results.Redirect(request.Url);
    }

    static async Task<IResult> CallbackAsync(
        HttpContext http,
        IOptionsMonitor<AuthConfig> auth,
        MalOAuthService oauth,
        MalAccountService accounts,
        ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger("Arrk.Mal.OAuth");

        string? error = http.Request.Query["error"].ToString();
        string mode = "login";
        if (!string.IsNullOrEmpty(error))
        {
            logger.LogWarning("MyAnimeList authorization failed: {Error}", error);
            return Results.Redirect("/login?error=mal");
        }

        string? code = http.Request.Query["code"];
        MalOAuthState? state = oauth.ReadState(http.Request, http.Request.Query["state"]);
        if (string.IsNullOrWhiteSpace(code) || state is null)
            return Results.Redirect("/login?error=mal-state");

        // Single use: the nonce cookie is cleared whether or not the exchange succeeds.
        http.Response.Cookies.Delete(MalOAuthService.StateCookie, new CookieOptions { Path = MalOAuthService.CallbackPath });

        mode = state.Mode;
        MalUser user;
        MalTokenResponse tokens;
        try
        {
            (user, tokens) = await oauth.CompleteAsync(code, state, http.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MyAnimeList token exchange failed");
            return Results.Redirect(string.Equals(mode, "link", StringComparison.Ordinal)
                ? "/account?linkError=mal-failed"
                : "/login?error=mal");
        }

        ClaimsPrincipal principal = BuildPrincipal(user);
        IExternalIdentityResolver resolver = http.RequestServices.GetRequiredService<IExternalIdentityResolver>();

        if (string.Equals(mode, "link", StringComparison.Ordinal))
        {
            int? currentUserId = int.TryParse(http.User.FindFirst(ArrkClaims.UserIdClaim)?.Value, out int signedIn)
                ? signedIn
                : null;
            if (state.UserId is not int linkUserId || currentUserId != linkUserId)
                return Results.Redirect("/login");

            MalAccount? existing = await accounts.GetByMalUserIdAsync(user.Id, http.RequestAborted);
            if (existing is not null && existing.UserId != linkUserId)
                return Results.Redirect("/account?linkError=mal-taken");

            bool linked = await resolver.LinkAsync(linkUserId, MalLoginProvider.Scheme, principal, http.RequestAborted);
            if (!linked)
                return Results.Redirect("/account?linkError=mal-taken");

            await accounts.SaveAsync(linkUserId, user, tokens, http.RequestAborted);
            return Results.Redirect("/account?linked=mal");
        }

        ExternalSignInResult result = await resolver.SignInAsync(
            MalLoginProvider.Scheme, principal, http.RequestAborted);

        if (result.UserId is int loginUserId)
        {
            MalAccount? existing = await accounts.GetByMalUserIdAsync(user.Id, http.RequestAborted);
            if (existing is not null && existing.UserId != loginUserId)
                return Results.Redirect("/login?error=mal-taken");
            await accounts.SaveAsync(loginUserId, user, tokens, http.RequestAborted);
        }

        if (result.Principal is null)
        {
            string codeValue = result.Status switch
            {
                ExternalSignInStatus.Pending => "pending",
                ExternalSignInStatus.Disabled => "disabled",
                _ => "mal-unlinked"
            };
            return Results.Redirect($"/login?error={codeValue}");
        }

        int sessionDays = auth.CurrentValue.SessionDays;
        AuthenticationProperties properties = new()
        {
            IsPersistent = true,
            AllowRefresh = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(Math.Clamp(sessionDays, 1, 365))
        };
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, result.Principal, properties);
        return Results.Redirect(Normalize(state.ReturnUrl));
    }

    static ClaimsPrincipal BuildPrincipal(MalUser user)
    {
        ClaimsIdentity identity = new(MalLoginProvider.Scheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Name));
        identity.AddClaim(new Claim("preferred_username", user.Name));
        identity.AddClaim(new Claim("username", user.Name));
        if (!string.IsNullOrWhiteSpace(user.PictureUrl))
            identity.AddClaim(new Claim("picture", user.PictureUrl));
        return new ClaimsPrincipal(identity);
    }

    static string Normalize(string? returnUrl) => LocalReturnUrl.Normalize(returnUrl, "/mal");
}
