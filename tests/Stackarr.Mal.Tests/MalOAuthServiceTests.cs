using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Mal.Auth;
using Stackarr.Mal.Clients;
using Stackarr.TestSupport;

namespace Stackarr.Mal.Tests;

public class MalOAuthServiceTests
{
    static MalOAuthService Create(ServiceProvider provider) => new(
        new MutableOptionsMonitor<MalConfig>(new MalConfig
        {
            ClientId = "client",
            ClientSecret = "secret",
            RedirectUris = ["https://example.test/mal-signin"]
        }),
        new MalClient(
            new MutableOptionsMonitor<MalConfig>(new MalConfig { ClientId = "client", ClientSecret = "secret" }),
            NullLogger<MalClient>.Instance),
        provider.GetRequiredService<IDataProtectionProvider>(),
        NullLogger<MalOAuthService>.Instance);

    static string StateCookieFrom(DefaultHttpContext http)
    {
        string setCookie = http.Response.Headers.SetCookie.ToString();
        string prefix = MalOAuthService.StateCookie + "=";
        int start = setCookie.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return "";
        int valueStart = start + prefix.Length;
        int end = setCookie.IndexOf(';', valueStart);
        return end < 0 ? setCookie[valueStart..] : setCookie[valueStart..end];
    }

    [Test]
    public async Task State_RoundTripsModeUserAndRedirect_AndSetsTheBrowserNonce()
    {
        using ServiceProvider provider = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        MalOAuthService oauth = Create(provider);

        DefaultHttpContext http = new();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("example.test");

        MalOAuthAuthorizeRequest request = oauth.CreateAuthorizeRequest(http.Request, http.Response, "link", 7, "/account");
        string nonce = StateCookieFrom(http);
        http.Request.Headers.Cookie = $"{MalOAuthService.StateCookie}={nonce}";

        MalOAuthState? state = oauth.ReadState(http.Request, request.State);

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.Mode).IsEqualTo("link");
        await Assert.That(state.UserId).IsEqualTo(7);
        await Assert.That(state.RedirectUri).IsEqualTo("https://example.test/mal-signin");
        await Assert.That(request.Url).Contains("code_challenge_method=plain");
        await Assert.That(nonce).IsNotEmpty();
    }

    [Test]
    public async Task State_WithoutTheBrowserNonce_IsRejected()
    {
        using ServiceProvider provider = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        MalOAuthService oauth = Create(provider);

        DefaultHttpContext http = new();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("example.test");

        MalOAuthAuthorizeRequest request = oauth.CreateAuthorizeRequest(http.Request, http.Response, "login", null, "/");
        // No cookie in the callback request: this is the login-CSRF case.
        MalOAuthState? state = oauth.ReadState(http.Request, request.State);

        await Assert.That(state).IsNull();
    }

    [Test]
    public async Task State_WithADifferentBrowserNonce_IsRejected()
    {
        using ServiceProvider provider = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        MalOAuthService oauth = Create(provider);

        DefaultHttpContext http = new();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("example.test");

        MalOAuthAuthorizeRequest request = oauth.CreateAuthorizeRequest(http.Request, http.Response, "login", null, "/");
        http.Request.Headers.Cookie = $"{MalOAuthService.StateCookie}=someone-elses-browser";

        MalOAuthState? state = oauth.ReadState(http.Request, request.State);

        await Assert.That(state).IsNull();
    }
}
