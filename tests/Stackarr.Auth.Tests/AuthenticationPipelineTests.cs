using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stackarr.Auth.Services;
using Stackarr.Auth.Abstractions;

namespace Stackarr.Auth.Tests;

/// <summary>
/// End-to-end tests of the real cookie authentication pipeline: sign-in, role authorization,
/// sign-out, disabled/blocked paths and the password-login toggle.
/// </summary>
public class AuthenticationPipelineTests
{
    static async Task<string> BodyAsync(HttpResponseMessage response)
        => await response.Content.ReadAsStringAsync();

    [Test]
    public async Task LoginPage_ShowsPasswordFormByDefault()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        HttpResponseMessage response = await host.Client.GetAsync("/login");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await BodyAsync(response);
        await Assert.That(body).Contains("action=\"/account/login\"");
    }

    [Test]
    public async Task LoginPage_HidesPasswordFormAndLocalSignUp_WhenPasswordLoginDisabled()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowPasswordLogin"] = "false");

        string body = await BodyAsync(await host.Client.GetAsync("/login"));

        await Assert.That(body).DoesNotContain("action=\"/account/login\"");
        await Assert.That(body).DoesNotContain("href=\"/register\"");
        await Assert.That(body).DoesNotContain("Create an account");
    }

    [Test]
    public async Task LoginPage_ShowsPasswordFormButHidesLocalSignUp_WhenLocalSignUpDisabled()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowLocalSignUp"] = "false");

        string body = await BodyAsync(await host.Client.GetAsync("/login"));

        await Assert.That(body).Contains("action=\"/account/login\"");
        await Assert.That(body).DoesNotContain("href=\"/register\"");
        await Assert.That(body).DoesNotContain("Create an account");
    }

    [Test]
    public async Task PasswordLoginPost_IsIgnored_WhenDisabled()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowPasswordLogin"] = "false");

        await host.LoginAsync("admin", AuthTestHost.AdminPassword);

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/admin"))).DoesNotContain("admin-ok");
    }

    [Test]
    public async Task Unauthenticated_CannotAccessAdminEndpoint()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/admin"))).DoesNotContain("admin-ok");
    }

    [Test]
    public async Task Login_WithValidAdminCredentials_GrantsAdminAccess()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        await host.LoginAsync("admin", AuthTestHost.AdminPassword);
        HttpResponseMessage admin = await host.Client.GetAsync("/admin");

        await Assert.That(admin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(admin)).IsEqualTo("admin-ok");
    }

    [Test]
    public async Task Login_WithInvalidCredentials_DoesNotAuthenticate()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        await host.LoginAsync("admin", "wrong");

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/admin"))).DoesNotContain("admin-ok");
    }

    [Test]
    public async Task Login_AsStandardUser_IsForbiddenFromAdminButAuthenticated()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.Users.CreateAsync("bob", "bob-pass", UserRole.User);

        await host.LoginAsync("bob", "bob-pass");

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/admin"))).DoesNotContain("admin-ok");
        HttpResponseMessage whoami = await host.Client.GetAsync("/whoami");
        await Assert.That(whoami.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(whoami)).IsEqualTo("bob");
    }

    [Test]
    public async Task Logout_RevokesAccess()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.LoginAsync("admin", AuthTestHost.AdminPassword);
        await Assert.That(await BodyAsync(await host.Client.GetAsync("/admin"))).IsEqualTo("admin-ok");

        await host.Client.GetAsync("/account/logout");

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/admin"))).DoesNotContain("admin-ok");
    }

    [Test]
    public async Task DisabledAccount_CannotSignIn()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser user = await host.Users.CreateAsync("carol", "carol-pass", UserRole.User);
        await host.Users.SetActiveAsync(user.Id, false);

        await host.LoginAsync("carol", "carol-pass");

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/whoami"))).DoesNotContain("carol");
    }

    [Test]
    public async Task DisablingAnAccount_RevokesAnExistingSession()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser user = await host.Users.CreateAsync("erin", "erin-pass", UserRole.User);
        await host.LoginAsync("erin", "erin-pass");
        await Assert.That(await BodyAsync(await host.Client.GetAsync("/whoami"))).IsEqualTo("erin");

        await host.Users.SetActiveAsync(user.Id, false);

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/whoami"))).DoesNotContain("erin");
    }

    [Test]
    public async Task ChangingPassword_RevokesExistingSessions()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser user = await host.Users.CreateAsync("frank", "frank-pass", UserRole.User);
        await host.LoginAsync("frank", "frank-pass");

        await host.Users.ChangePasswordAsync(user.Id, "frank-pass", "frank-new-pass");

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/whoami"))).DoesNotContain("frank");
    }

    [Test]
    public async Task DemotingToPending_RevokesTheSession()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser user = await host.Users.CreateAsync("gina", "gina-pass", UserRole.User);
        await host.LoginAsync("gina", "gina-pass");

        await host.Users.SetRoleAsync(user.Id, UserRole.Pending);

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/whoami"))).DoesNotContain("gina");
    }

    [Test]
    public async Task PromotingToAdmin_RefreshesTheCookieRole()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser user = await host.Users.CreateAsync("hank", "hank-pass", UserRole.User);
        await host.LoginAsync("hank", "hank-pass");
        await Assert.That(await BodyAsync(await host.Client.GetAsync("/admin"))).DoesNotContain("admin-ok");

        await host.Users.SetRoleAsync(user.Id, UserRole.Admin);

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/admin"))).IsEqualTo("admin-ok");
    }

    [Test]
    public async Task LoginPost_WithoutAntiforgeryToken_IsRejected()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        await host.Client.PostAsync("/account/login", AuthTestHost.LoginForm("admin", AuthTestHost.AdminPassword));

        await Assert.That(await BodyAsync(await host.Client.GetAsync("/admin"))).DoesNotContain("admin-ok");
    }

    [Test]
    public async Task ConfigSchema_IsOnlyAvailableToAdmins()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.Users.CreateAsync("ivy", "ivy-pass", UserRole.User);

        await Assert.That((await host.Client.GetAsync("/api/config/schema")).IsSuccessStatusCode).IsFalse();

        await host.LoginAsync("ivy", "ivy-pass");
        await Assert.That((await host.Client.GetAsync("/api/config/schema")).IsSuccessStatusCode).IsFalse();

        await host.Client.GetAsync("/account/logout");
        await host.LoginAsync("admin", AuthTestHost.AdminPassword);
        await Assert.That((await host.Client.GetAsync("/api/config/schema")).IsSuccessStatusCode).IsTrue();
    }

    [Test]
    public async Task ConfigValidation_IsOnlyAvailableToAdmins()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.Users.CreateAsync("jack", "jack-pass", UserRole.User);

        static System.Net.Http.Json.JsonContent Body() => System.Net.Http.Json.JsonContent.Create(
            new { content = "auth:\n  sessionDays: 7\n" });

        await Assert.That((await host.Client.PostAsync("/api/config/validate", Body())).IsSuccessStatusCode).IsFalse();

        await host.LoginAsync("jack", "jack-pass");
        await Assert.That((await host.Client.PostAsync("/api/config/validate", Body())).IsSuccessStatusCode).IsFalse();

        await host.Client.GetAsync("/account/logout");
        await host.LoginAsync("admin", AuthTestHost.AdminPassword);
        await Assert.That((await host.Client.PostAsync("/api/config/validate", Body())).IsSuccessStatusCode).IsTrue();
    }

    [Test]
    public async Task PasswordLogin_IsRateLimitedPerClient()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:authRateLimitPerMinute"] = "2");

        await host.LoginAsync("admin", "wrong");
        await host.LoginAsync("admin", "wrong");
        HttpResponseMessage third = await host.Client.PostAsync(
            "/account/login", AuthTestHost.LoginForm("admin", "wrong"));

        await Assert.That(third.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task Session_SurvivesRestart_WhenDataProtectionKeysArePersisted()
    {
        string dataProtectionDir = Path.Combine(Path.GetTempPath(), $"stackarr-dp-{Guid.NewGuid():N}");
        await using TestDatabase database = new();
        string? cookie;

        await using (AuthTestHost first = await AuthTestHost.CreateAsync(
            dataProtectionDir: dataProtectionDir, database: database))
        {
            await first.LoginAsync("admin", AuthTestHost.AdminPassword);
            cookie = first.GetCookie("stackarr.auth");
            await Assert.That(cookie).IsNotNull();
        }

        // A fresh process sharing the key ring and database must accept the existing session cookie.
        await using (AuthTestHost restarted = await AuthTestHost.CreateAsync(
            dataProtectionDir: dataProtectionDir, database: database))
        {
            restarted.SetCookie("stackarr.auth", cookie!);
            HttpResponseMessage admin = await restarted.Client.GetAsync("/admin");
            await Assert.That(admin.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await BodyAsync(admin)).IsEqualTo("admin-ok");
        }

        try { Directory.Delete(dataProtectionDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Login_UpdatesLastLoginTimestamp()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser user = await host.Users.CreateAsync("dave", "dave-pass", UserRole.User);

        await host.LoginAsync("dave", "dave-pass");

        await Assert.That((await host.Users.GetByIdAsync(user.Id))!.LastLoginAt).IsNotNull();
    }

    [Test]
    public async Task LoginPage_LinksToAccountCreationByDefault()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        string body = await BodyAsync(await host.Client.GetAsync("/login"));

        await Assert.That(body).Contains("href=\"/register\"");
        await Assert.That(body).Contains("Create an account");
    }

    [Test]
    public async Task LoginPage_HidesRegistrationInAdminOnlyMode()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "AdminOnly");

        string body = await BodyAsync(await host.Client.GetAsync("/login"));

        await Assert.That(body).DoesNotContain("href=\"/register\"");
    }

    [Test]
    public async Task RegisterPage_IsServedInApprovalMode()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        HttpResponseMessage response = await host.Client.GetAsync("/register");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await BodyAsync(response);
        await Assert.That(body).Contains("action=\"/account/register\"");
        await Assert.That(body).Contains("administrator must approve");
    }

    [Test]
    public async Task RegisterPage_RedirectsToLoginInAdminOnlyMode()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "AdminOnly");

        HttpResponseMessage response = await host.Client.GetAsync("/register");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/login");
    }

    [Test]
    public async Task RegisterPage_RedirectsToLoginWhenPasswordLoginDisabled()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowPasswordLogin"] = "false");

        HttpResponseMessage response = await host.Client.GetAsync("/register");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/login");
    }

    [Test]
    public async Task RegisterPage_RedirectsToLoginWhenLocalSignUpDisabled()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowLocalSignUp"] = "false");

        HttpResponseMessage response = await host.Client.GetAsync("/register");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/login");
    }

    [Test]
    public async Task RegisterPost_WhenLocalSignUpDisabled_CreatesNothing()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowLocalSignUp"] = "false");

        await host.RegisterAsync("newbie", "hunter2pass");

        await Assert.That(await host.Users.GetByUsernameAsync("newbie")).IsNull();
    }

    [Test]
    public async Task RegisterPost_WhenPasswordLoginDisabled_CreatesNothing()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowPasswordLogin"] = "false");

        await host.RegisterAsync("newbie", "hunter2pass");

        await Assert.That(await host.Users.GetByUsernameAsync("newbie")).IsNull();
    }

    [Test]
    public async Task RegisterPost_InApprovalMode_CreatesPendingAccountWithoutSession()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        HttpResponseMessage response = await host.RegisterAsync("newbie", "hunter2pass");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/login?notice=pending");

        StackarrUser? user = await host.Users.GetByUsernameAsync("newbie");
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.Role).IsEqualTo(UserRole.Pending);
        await Assert.That(await (await host.Client.GetAsync("/whoami")).Content.ReadAsStringAsync())
            .DoesNotContain("newbie");
    }

    [Test]
    public async Task PendingAccount_CannotSignInWithPassword()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.Registration.RegisterAsync("newbie", "hunter2pass");

        HttpResponseMessage response = await host.LoginAsync("newbie", "hunter2pass");

        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo("/login?error=pending");
        await Assert.That(await (await host.Client.GetAsync("/whoami")).Content.ReadAsStringAsync())
            .DoesNotContain("newbie");
    }

    [Test]
    public async Task ApprovedAccount_CanSignInWithPassword()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        RegistrationOutcome signup = await host.Registration.RegisterAsync("newbie", "hunter2pass");
        await host.Users.SetRoleAsync(signup.User.Id, UserRole.User);

        await host.LoginAsync("newbie", "hunter2pass");

        HttpResponseMessage whoami = await host.Client.GetAsync("/whoami");
        await Assert.That(whoami.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(whoami)).IsEqualTo("newbie");
    }

    [Test]
    public async Task RegisterPost_InOpenMode_CreatesAccountAndSignsIn()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "Open");

        await host.RegisterAsync("newbie", "hunter2pass");

        HttpResponseMessage whoami = await host.Client.GetAsync("/whoami");
        await Assert.That(whoami.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(whoami)).IsEqualTo("newbie");
    }

    [Test]
    public async Task RegisterPost_InAdminOnlyMode_CreatesNothing()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "AdminOnly");

        await host.RegisterAsync("newbie", "hunter2pass");

        await Assert.That(await host.Users.GetByUsernameAsync("newbie")).IsNull();
    }

    [Test]
    public async Task RegisterPost_WithMismatchedConfirmation_CreatesNothing()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        await host.RegisterAsync("newbie", "hunter2pass", confirmPassword: "different");

        await Assert.That(await host.Users.GetByUsernameAsync("newbie")).IsNull();
    }

    [Test]
    public async Task AccountLinkEndpoint_RequiresAuthentication()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        HttpResponseMessage response = await host.Client.GetAsync("/account/link/discord");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.ToString()).Contains("/login");
    }

    [Test]
    public async Task DiscordOidc_RequestsIdentifyButNotEmail()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        OpenIdConnectOptions options = host.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(StackarrAuthenticationExtensions.DiscordScheme);

        await Assert.That(options.Scope.Contains("openid")).IsTrue();
        await Assert.That(options.Scope.Contains("identify")).IsTrue();
        await Assert.That(options.Scope.Contains("email")).IsFalse();
    }

    [Test]
    public async Task PendingRole_IsDeniedByTheDefaultPolicy()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        IAuthorizationService authorization = host.Services.GetRequiredService<IAuthorizationService>();
        AuthorizationPolicy defaultPolicy = host.Services
            .GetRequiredService<IOptions<AuthorizationOptions>>().Value.DefaultPolicy;

        ClaimsPrincipal pending = Principal(StackarrRoles.Pending);
        ClaimsPrincipal user = Principal(StackarrRoles.User);
        ClaimsPrincipal admin = Principal(StackarrRoles.Admin);

        await Assert.That((await authorization.AuthorizeAsync(pending, null, defaultPolicy)).Succeeded).IsFalse();
        await Assert.That((await authorization.AuthorizeAsync(user, null, defaultPolicy)).Succeeded).IsTrue();
        await Assert.That((await authorization.AuthorizeAsync(admin, null, defaultPolicy)).Succeeded).IsTrue();
    }

    static ClaimsPrincipal Principal(string role)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "someone"), new Claim(ClaimTypes.Role, role)],
            authenticationType: "test"));
}
