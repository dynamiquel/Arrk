using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Mal.Clients;

namespace Stackarr.Mal.Auth;

public sealed record MalOAuthState(
    string Mode,
    int? UserId,
    string ReturnUrl,
    string Verifier,
    string RedirectUri,
    DateTime CreatedAt,
    string Nonce);

public sealed record MalOAuthAuthorizeRequest(string Url, string State);

/// <summary>
/// Builds and validates the MAL authorization-code + PKCE flow. The state is a protected payload
/// (no cookie): it carries the verifier, mode, user binding and the exact redirect URI used, so a
/// callback cannot be replayed or attached to another account.
/// </summary>
public sealed class MalOAuthService(
    IOptionsMonitor<MalConfig> config,
    MalClient client,
    IDataProtectionProvider dataProtection,
    ILogger<MalOAuthService> logger)
{
    public const string CallbackPath = "/mal-signin";

    /// <summary>Browser-bound nonce cookie that turns the signed state into a CSRF-resistant flow.</summary>
    public const string StateCookie = "stackarr.mal.state";

    static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(20);
    readonly IDataProtector _states = dataProtection.CreateProtector("Stackarr.Mal.OAuthState");

    public bool IsConfigured => config.CurrentValue.IsConfigured;

    public string ResolveRedirectUri(HttpRequest request)
    {
        MalConfig current = config.CurrentValue;
        string origin = $"{request.Scheme}://{request.Host.Value}";
        string expected = origin.TrimEnd('/') + CallbackPath;

        foreach (string candidate in current.RedirectUris)
        {
            if (string.Equals(candidate.TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase))
                return candidate.TrimEnd('/');
        }

        if (current.RedirectUris.Count > 0)
            return current.RedirectUris[0].TrimEnd('/');

        // No explicit allow-list: stay on the origin the user actually opened. Forwarded headers
        // make this the public HTTPS origin when running behind Traefik/Cloudflare.
        return expected.TrimEnd('/');
    }

    public MalOAuthAuthorizeRequest CreateAuthorizeRequest(
        HttpRequest request,
        HttpResponse response,
        string mode,
        int? userId,
        string? returnUrl)
    {
        string verifier = CreateVerifier();
        string nonce = CreateVerifier();
        string redirectUri = ResolveRedirectUri(request);
        MalOAuthState state = new(
            mode,
            userId,
            string.IsNullOrWhiteSpace(returnUrl) ? "/mal" : returnUrl,
            verifier,
            redirectUri,
            DateTime.UtcNow,
            nonce);

        // The protected state alone can be replayed or sent to another browser (login CSRF). The
        // nonce cookie binds the callback to the browser that started the flow.
        response.Cookies.Append(StateCookie, nonce, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = request.IsHttps,
            Path = CallbackPath,
            MaxAge = StateLifetime
        });

        string protectedState = _states.Protect(JsonSerializer.Serialize(state));
        MalConfig current = config.CurrentValue;
        string url = $"{current.OAuthBaseUrl.TrimEnd('/')}/authorize" +
                     $"?response_type=code" +
                     $"&client_id={Uri.EscapeDataString(current.ClientId)}" +
                     $"&state={Uri.EscapeDataString(protectedState)}" +
                     $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                     $"&code_challenge={Uri.EscapeDataString(verifier)}" +
                     "&code_challenge_method=plain";

        return new MalOAuthAuthorizeRequest(url, protectedState);
    }

    public MalOAuthState? ReadState(HttpRequest request, string? protectedState)
    {
        if (string.IsNullOrWhiteSpace(protectedState))
            return null;

        string? cookieNonce = request.Cookies[StateCookie];
        if (string.IsNullOrEmpty(cookieNonce))
        {
            logger.LogWarning("MyAnimeList OAuth callback has no browser-bound state cookie");
            return null;
        }

        try
        {
            MalOAuthState? state = JsonSerializer.Deserialize<MalOAuthState>(_states.Unprotect(protectedState));
            if (state is null
                || DateTime.UtcNow - state.CreatedAt > StateLifetime
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(cookieNonce),
                    System.Text.Encoding.UTF8.GetBytes(state.Nonce ?? "")))
            {
                return null;
            }

            return state;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read MyAnimeList OAuth state");
            return null;
        }
    }

    public async Task<(MalUser User, MalTokenResponse Tokens)> CompleteAsync(
        string code,
        MalOAuthState state,
        CancellationToken ct)
    {
        MalTokenResponse tokens = await client.ExchangeCodeAsync(code, state.RedirectUri, state.Verifier, ct);
        MalUser user = await client.GetMeAsync(tokens.AccessToken, ct);
        return (user, tokens);
    }

    static string CreateVerifier()
    {
        Span<byte> bytes = stackalloc byte[48];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
