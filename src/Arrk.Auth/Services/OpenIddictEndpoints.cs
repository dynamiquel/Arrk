using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Arrk.Auth.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Arrk.Auth.Services;

/// <summary>
/// Implements the OpenID Connect provider endpoints the media services talk to. Discovery and
/// JWKS are served by the OpenIddict middleware; the endpoints here provide the user-facing
/// authorization/token/userinfo behaviour.
/// </summary>
public static class OpenIddictEndpoints
{
    public static void MapArrkOpenIddictEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/connect/authorize", AuthorizeAsync).AllowAnonymous();
        endpoints.MapPost("/connect/token", TokenAsync).AllowAnonymous().DisableAntiforgery();
        endpoints.MapGet("/connect/userinfo", UserInfoAsync).AllowAnonymous();
        endpoints.MapPost("/connect/userinfo", UserInfoAsync).AllowAnonymous().DisableAntiforgery();
    }

    static async Task<IResult> AuthorizeAsync(HttpContext http, UserService users)
    {
        OpenIddictRequest request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (http.User.Identity?.IsAuthenticated != true)
        {
            // Send the browser to the Arrk login page, then back here.
            AuthenticationProperties properties = new()
            {
                RedirectUri = http.Request.PathBase + http.Request.Path + http.Request.QueryString
            };
            return Results.Challenge(properties, [CookieAuthenticationDefaults.AuthenticationScheme]);
        }

        ArrkUser? user = await ResolveUserAsync(http, users);
        if (user is null || !user.IsActive || user.Role == UserRole.Pending)
            return Results.Forbid();

        ClaimsPrincipal principal = await BuildPrincipalAsync(user, users, request.GetScopes());
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    static async Task<IResult> TokenAsync(HttpContext http, UserService users)
    {
        OpenIddictRequest request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return Results.BadRequest(new { error = Errors.UnsupportedGrantType });

        AuthenticateResult result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
            return Results.Forbid();

        string? subject = result.Principal.GetClaim(Claims.Subject);
        ArrkUser? user = int.TryParse(subject, out int userId)
            ? await users.GetByIdAsync(userId)
            : null;
        if (user is null || !user.IsActive || user.Role == UserRole.Pending)
            return Results.Forbid();

        ClaimsPrincipal principal = await BuildPrincipalAsync(user, users, result.Principal.GetScopes());
        principal.SetResources(result.Principal.GetResources());
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    static async Task<IResult> UserInfoAsync(HttpContext http, UserService users)
    {
        AuthenticateResult result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
            return Results.Challenge(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

        string? subject = result.Principal.GetClaim(Claims.Subject);
        ArrkUser? user = int.TryParse(subject, out int userId)
            ? await users.GetByIdAsync(userId)
            : null;
        if (user is null || !user.IsActive || user.Role == UserRole.Pending)
            return Results.Forbid();

        IReadOnlyList<string> roles = await users.GetRolesAsync(user);
        Dictionary<string, object?> payload = new()
        {
            ["sub"] = user.Id.ToString(),
            ["name"] = user.DisplayName,
            ["preferred_username"] = user.Username,
            ["roles"] = roles
        };

        if (!string.IsNullOrEmpty(user.Email))
            payload["email"] = user.Email;

        return Results.Json(payload);
    }

    static async Task<ArrkUser?> ResolveUserAsync(HttpContext http, UserService users)
    {
        string? id = http.User.FindFirst(ArrkClaims.UserIdClaim)?.Value;
        return int.TryParse(id, out int userId) ? await users.GetByIdAsync(userId) : null;
    }

    static async Task<ClaimsPrincipal> BuildPrincipalAsync(ArrkUser user, UserService users, IEnumerable<string> scopes)
    {
        ClaimsIdentity identity = new(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.AddClaim(Claims.Subject, user.Id.ToString());
        identity.AddClaim(Claims.Name, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName);
        identity.AddClaim(Claims.PreferredUsername, user.Username);

        if (!string.IsNullOrEmpty(user.Email))
            identity.AddClaim(Claims.Email, user.Email);

        foreach (string role in await users.GetRolesAsync(user))
            identity.AddClaim("roles", role);

        ClaimsPrincipal principal = new(identity);
        principal.SetScopes(scopes);
        // Token audience is a wire contract shared with existing OIDC clients.
        principal.SetResources("stackarr");

        foreach (Claim claim in principal.Claims)
            claim.SetDestinations(GetDestinations(claim, principal));

        return principal;
    }

    static IEnumerable<string> GetDestinations(Claim claim, ClaimsPrincipal principal)
    {
        switch (claim.Type)
        {
            case Claims.Name:
            case Claims.PreferredUsername:
                yield return Destinations.AccessToken;
                if (principal.HasScope(Scopes.Profile))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (principal.HasScope(Scopes.Email))
                    yield return Destinations.IdentityToken;
                yield break;

            case "roles":
                yield return Destinations.AccessToken;
                if (principal.HasScope("roles"))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Subject:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
