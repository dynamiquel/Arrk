using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Arrk.Auth.Abstractions;
using Arrk.Auth.Services;
using Arrk.Core.Services;

namespace Arrk.Auth.Tests;

/// <summary>
/// End-to-end tests of Arrk as an OpenID Connect provider: discovery, JWKS, the client
/// registration and a complete authorization-code + PKCE exchange through to userinfo.
/// </summary>
public class OpenIddictTests
{
    const string RedirectUri = "http://localhost/callback";

    [Test]
    public async Task DiscoveryDocument_IsServed()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        HttpResponseMessage response = await host.Client.GetAsync("/.well-known/openid-configuration");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        await Assert.That(doc.GetProperty("issuer").GetString()!.TrimEnd('/')).IsEqualTo("http://localhost");
        await Assert.That(doc.GetProperty("authorization_endpoint").GetString()).EndsWith("/connect/authorize");
        await Assert.That(doc.GetProperty("token_endpoint").GetString()).EndsWith("/connect/token");
        await Assert.That(doc.GetProperty("userinfo_endpoint").GetString()).EndsWith("/connect/userinfo");
        await Assert.That(doc.GetProperty("jwks_uri").GetString()).IsNotEmpty();
    }

    [Test]
    public async Task Jwks_ExposesSigningKeys()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        JsonElement doc = JsonDocument.Parse(await (await host.Client.GetAsync("/.well-known/openid-configuration")).Content.ReadAsStringAsync()).RootElement;
        string jwksUri = doc.GetProperty("jwks_uri").GetString()!;
        Uri uri = new(new Uri("http://localhost"), jwksUri);

        HttpResponseMessage response = await host.Client.GetAsync(uri.PathAndQuery);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement jwks = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        await Assert.That(jwks.GetProperty("keys").GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public async Task JellyfinClient_IsRegistered()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        IOpenIddictApplicationManager manager = host.Services.GetRequiredService<IOpenIddictApplicationManager>();
        object? app = await manager.FindByClientIdAsync("jellyfin");

        await Assert.That(app).IsNotNull();
        await Assert.That(await manager.GetRedirectUrisAsync(app!))
            .Contains("http://localhost/jellyfin/sso/OIDC/Callback/stackarr");
        await Assert.That(await manager.GetRedirectUrisAsync(app!))
            .Contains("http://localhost/jellyfin/sso/OIDC/Auth/stackarr");
    }

    [Test]
    public async Task ExistingDatabase_RetainsAccountsAndOidcClientIdentityAndSecret()
    {
        await using TestDatabase database = new();
        string? clientId;
        string? secret;
        await using (AuthTestHost first = await AuthTestHost.CreateAsync(database: database))
        {
            await first.Users.CreateAsync("existing-user", "existing-pass", UserRole.User);
            IOpenIddictApplicationManager manager = first.Services.GetRequiredService<IOpenIddictApplicationManager>();
            object application = (await manager.FindByClientIdAsync("jellyfin"))!;
            clientId = await manager.GetIdAsync(application);
            secret = await first.Services.GetRequiredService<SettingsService>()
                .GetAsync(OidcClientSeeder.JellyfinSecretKey);
            await Assert.That(secret).IsNotNull();
            await Assert.That(await manager.ValidateClientSecretAsync(application, secret!)).IsTrue();
        }

        // Simulate a database created before the rename: callbacks retain the historical id.
        await using AuthTestHost restarted = await AuthTestHost.CreateAsync(database: database);
        IOpenIddictApplicationManager reloaded = restarted.Services.GetRequiredService<IOpenIddictApplicationManager>();
        object existing = (await reloaded.FindByClientIdAsync("jellyfin"))!;
        await Assert.That(await reloaded.GetIdAsync(existing)).IsEqualTo(clientId);
        await Assert.That(await reloaded.ValidateClientSecretAsync(existing, secret!)).IsTrue();
        await Assert.That(await reloaded.GetRedirectUrisAsync(existing))
            .Contains("http://localhost/jellyfin/sso/OIDC/Callback/stackarr");
        await restarted.LoginAsync("existing-user", "existing-pass");
        HttpResponseMessage whoami = await restarted.Client.GetAsync("/whoami");
        await Assert.That(whoami.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await whoami.Content.ReadAsStringAsync()).IsEqualTo("existing-user");
    }

    [Test]
    public async Task AuthorizationCodeFlow_WithPkce_IssuesTokensAndUserInfo()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.EnsureClientAsync("test-client", "test-secret", RedirectUri);
        await host.Users.CreateAsync("alice", "alice-pass", UserRole.User);

        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string authorizeUrl =
            $"/connect/authorize?client_id=test-client&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&response_type=code&scope={Uri.EscapeDataString("openid profile email roles")}" +
            $"&state=xyz&code_challenge={challenge}&code_challenge_method=S256";

        // Unauthenticated access challenges to the login page with the original URL preserved.
        HttpResponseMessage challengeResponse = await host.Client.GetAsync(authorizeUrl);
        await Assert.That(challengeResponse.StatusCode).IsEqualTo(HttpStatusCode.Found);
        string challengeLocation = challengeResponse.Headers.Location!.ToString();
        await Assert.That(challengeLocation).Contains("/login");
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> challengeQuery =
            QueryHelpers.ParseQuery(new Uri(new Uri("http://localhost"), challengeLocation).Query);
        string returnUrl = challengeQuery["ReturnUrl"].ToString();
        await Assert.That(returnUrl).Contains("/connect/authorize");

        // Sign in, then confirm the browser is sent back to the authorize endpoint (resuming the flow).
        await host.LoginAsync("alice", "alice-pass", returnUrl);
        HttpResponseMessage resumed = await host.Client.GetAsync(returnUrl);
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.Found);

        // Authorized request returns a code.
        HttpResponseMessage authorize = await host.Client.GetAsync(authorizeUrl);
        await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Found);

        Uri location = authorize.Headers.Location!;
        if (!location.IsAbsoluteUri)
            location = new Uri(new Uri("http://localhost"), location);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(location.Query);
        string code = query["code"].ToString();
        await Assert.That(code).IsNotEmpty();
        await Assert.That(query["state"].ToString()).IsEqualTo("xyz");

        // Exchange the code (with the PKCE verifier) for tokens.
        HttpResponseMessage tokenResponse = await host.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = "test-client",
                ["client_secret"] = "test-secret",
                ["code_verifier"] = verifier
            }));

        await Assert.That(tokenResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync()).RootElement;
        string accessToken = tokens.GetProperty("access_token").GetString()!;
        await Assert.That(accessToken).IsNotEmpty();
        await Assert.That(tokens.TryGetProperty("id_token", out JsonElement idToken)).IsTrue();

        // The id token carries the roles claim.
        string payload = Encoding.UTF8.GetString(Base64UrlDecode(idToken.GetString()!.Split('.')[1]));
        JsonElement idPayload = JsonDocument.Parse(payload).RootElement;
        JsonElement idRoles = idPayload.GetProperty("roles");
        List<string> roleList = idRoles.ValueKind == JsonValueKind.Array
            ? idRoles.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [idRoles.GetString()!];
        await Assert.That(roleList).Contains("User");

        // Userinfo returns the identity.
        HttpRequestMessage userInfoRequest = new(HttpMethod.Get, "/connect/userinfo");
        userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage userInfo = await host.Client.SendAsync(userInfoRequest);

        await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement info = JsonDocument.Parse(await userInfo.Content.ReadAsStringAsync()).RootElement;
        await Assert.That(info.GetProperty("preferred_username").GetString()).IsEqualTo("alice");
        await Assert.That(info.GetProperty("roles").EnumerateArray().Select(x => x.GetString()).ToList()).Contains("User");
    }

    [Test]
    public async Task AuthorizationCodeFlow_WithoutPkce_IsRejected()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.EnsureClientAsync("test-client", "test-secret", RedirectUri);
        await host.Users.CreateAsync("alice", "alice-pass", UserRole.User);
        await host.LoginAsync("alice", "alice-pass");

        string authorizeUrl =
            $"/connect/authorize?client_id=test-client&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&response_type=code&scope=openid&state=xyz";

        HttpResponseMessage response = await host.Client.GetAsync(authorizeUrl);

        // A missing code_challenge must not yield an authorization code.
        if (response.StatusCode == HttpStatusCode.Found)
            await Assert.That(response.Headers.Location!.ToString()).DoesNotContain("code=");
    }

    static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }
}
