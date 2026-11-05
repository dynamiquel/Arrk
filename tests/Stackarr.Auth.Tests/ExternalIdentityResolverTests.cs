using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Auth.Services;
using Stackarr.Auth.Abstractions;
using Stackarr.TestSupport;

namespace Stackarr.Auth.Tests;

/// <summary>Signing in with an upstream OIDC identity, including pending self-service sign-ups.</summary>
public class ExternalIdentityResolverTests
{
    static async Task<ExternalSignInResult> SignInAsync(
        AuthTestHost host,
        string subject,
        string username = "alice",
        string name = "Alice",
        string? email = null,
        string? picture = null)
    {
        // Production resolves the resolver per request; a fresh scope models that so role changes
        // made by another scope are observed immediately.
        using IServiceScope scope = host.Services.CreateScope();
        ExternalIdentityResolver resolver = scope.ServiceProvider.GetRequiredService<ExternalIdentityResolver>();
        return await resolver.SignInAsync("discord", Principal(subject, username, name, email, picture));
    }

    static async Task<bool> LinkAsync(AuthTestHost host, int userId, string subject, string? picture = null)
    {
        using IServiceScope scope = host.Services.CreateScope();
        ExternalIdentityResolver resolver = scope.ServiceProvider.GetRequiredService<ExternalIdentityResolver>();
        return await resolver.LinkAsync(userId, "discord", Principal(subject, picture: picture));
    }

    static ClaimsPrincipal Principal(
        string subject,
        string username = "alice",
        string name = "Alice",
        string? email = null,
        string? picture = null)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, subject),
            new("preferred_username", username),
            new("name", name)
        ];
        if (email is not null)
            claims.Add(new Claim(ClaimTypes.Email, email));
        if (picture is not null)
            claims.Add(new Claim("picture", picture));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "discord"));
    }

    [Test]
    public async Task SignInAsync_OpenMode_CreatesUserAndReturnsPrincipal()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "Open");

        ExternalSignInResult result = await SignInAsync(host, "sub-1");

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.SignedIn);
        await Assert.That(result.Principal).IsNotNull();
        await Assert.That(result.Principal!.Identity!.Name).IsEqualTo("alice");

        StackarrUser? user = await host.Users.GetByUsernameAsync("alice");
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.Role).IsEqualTo(UserRole.User);
        await Assert.That((await host.Users.GetExternalLoginsAsync(user.Id)).Any(l => l.ProviderKey == "sub-1")).IsTrue();
    }

    [Test]
    public async Task SignInAsync_ApprovalMode_CreatesPendingAccountWithoutSession()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        ExternalSignInResult result = await SignInAsync(host, "sub-pending");

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.Pending);
        await Assert.That(result.Principal).IsNull();

        StackarrUser? user = await host.Users.GetByUsernameAsync("alice");
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.Role).IsEqualTo(UserRole.Pending);
        await Assert.That((await host.Users.GetExternalLoginsAsync(user.Id)).Any(l => l.ProviderKey == "sub-pending")).IsTrue();
    }

    [Test]
    public async Task SignInAsync_ApprovalMode_WithLocalSignUpDisabled_StillCreatesPendingAccount()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowLocalSignUp"] = "false");

        ExternalSignInResult result = await SignInAsync(host, "sub-discord-signup");

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.Pending);
        StackarrUser? user = await host.Users.GetByUsernameAsync("alice");
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.Role).IsEqualTo(UserRole.Pending);
    }

    [Test]
    public async Task SignInAsync_ApprovalMode_WithPasswordLoginDisabled_StillCreatesPendingAccount()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowPasswordLogin"] = "false");

        ExternalSignInResult result = await SignInAsync(host, "sub-discord-only");

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.Pending);
        StackarrUser? user = await host.Users.GetByUsernameAsync("alice");
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.Role).IsEqualTo(UserRole.Pending);
    }

    [Test]
    public async Task SignInAsync_SecondLogin_ReturnsSameAccount()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "Open");

        ExternalSignInResult first = await SignInAsync(host, "sub-1");
        ExternalSignInResult second = await SignInAsync(host, "sub-1");

        await Assert.That(second.Principal!.Identity!.Name).IsEqualTo(first.Principal!.Identity!.Name);
    }

    [Test]
    public async Task SignInAsync_AfterApproval_ReturnsPrincipal()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        await SignInAsync(host, "sub-approve");
        StackarrUser pending = (await host.Users.GetByUsernameAsync("alice"))!;
        await host.Users.SetRoleAsync(pending.Id, UserRole.User);

        StackarrUser approved = (await host.Users.GetByUsernameAsync("alice"))!;
        await Assert.That(approved.Role).IsEqualTo(UserRole.User);

        ExternalSignInResult result = await SignInAsync(host, "sub-approve");

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.SignedIn);
        await Assert.That(result.Principal).IsNotNull();
    }

    [Test]
    public async Task SignInAsync_AdminOnlyMode_DoesNotCreateUnknownAccount()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "AdminOnly");

        ExternalSignInResult result = await SignInAsync(host, "new-sub");

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.Unknown);
        await Assert.That(await host.Users.GetByUsernameAsync("alice")).IsNull();
    }

    [Test]
    public async Task SignInAsync_WhenLinkedAccountIsDisabled_ReturnsDisabled()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser user = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);
        await host.Users.AddExternalLoginAsync(user.Id, "discord", "sub-disabled", "alice");
        await host.Users.SetActiveAsync(user.Id, false);

        ExternalSignInResult result = await SignInAsync(host, "sub-disabled");

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.Disabled);
        await Assert.That(result.Principal).IsNull();
    }

    [Test]
    public async Task SignInAsync_AdminCarriesRoles()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser preCreated = await host.Users.CreateAsync("bob", "hunter2pass", UserRole.Admin);
        await host.Users.AddExternalLoginAsync(preCreated.Id, "discord", "sub-bob", "bob");

        ExternalSignInResult result = await SignInAsync(host, "sub-bob", "bob", "Bob");

        await Assert.That(result.Principal).IsNotNull();
        await Assert.That(result.Principal!.IsInRole(StackarrRoles.Admin)).IsTrue();
        await Assert.That(result.Principal.Identity!.Name).IsEqualTo("bob");
    }

    [Test]
    public async Task SignInAsync_WithoutSubject_ReturnsUnknown()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim("name", "no-subject")], "discord"));

        using IServiceScope scope = host.Services.CreateScope();
        ExternalIdentityResolver resolver = scope.ServiceProvider.GetRequiredService<ExternalIdentityResolver>();
        ExternalSignInResult result = await resolver.SignInAsync("discord", principal);

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.Unknown);
    }

    [Test]
    public async Task LinkAsync_AttachesIdentityToExistingAccount()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser user = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);

        bool linked = await LinkAsync(host, user.Id, "sub-link");

        await Assert.That(linked).IsTrue();
        IReadOnlyList<Microsoft.AspNetCore.Identity.UserLoginInfo> logins =
            await host.Users.GetExternalLoginsAsync(user.Id);
        await Assert.That(logins.Any(l => l.ProviderKey == "sub-link")).IsTrue();
    }

    [Test]
    public async Task LinkAsync_WhenIdentityBelongsToAnotherAccount_ReturnsFalse()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        StackarrUser first = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);
        StackarrUser second = await host.Users.CreateAsync("bob", "hunter2pass", UserRole.User);
        await host.Users.AddExternalLoginAsync(first.Id, "discord", "sub-shared", "alice");

        bool linked = await LinkAsync(host, second.Id, "sub-shared");

        await Assert.That(linked).IsFalse();
        IReadOnlyList<Microsoft.AspNetCore.Identity.UserLoginInfo> logins =
            await host.Users.GetExternalLoginsAsync(second.Id);
        await Assert.That(logins).IsEmpty();
    }

    [Test]
    public async Task SignInAsync_UsesDiscordHandleNotAnEmailMappedNameClaim()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "Open");

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "sub-email"),
            new Claim("preferred_username", "alice"),
            new Claim(ClaimTypes.Name, "alice@example.com"),
            new Claim(ClaimTypes.Email, "alice@example.com")
        ], "discord"));

        using IServiceScope scope = host.Services.CreateScope();
        ExternalIdentityResolver resolver = scope.ServiceProvider.GetRequiredService<ExternalIdentityResolver>();
        ExternalSignInResult result = await resolver.SignInAsync("discord", principal);

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.SignedIn);
        StackarrUser? user = await host.Users.GetByUsernameAsync("alice");
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.Email).IsEqualTo("alice@example.com");
        await Assert.That(await host.Users.GetByUsernameAsync("alice-example.com")).IsNull();
    }

    [Test]
    public async Task SignInAsync_WithoutAHandleClaim_FallsBackToName()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "Open");

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "sub-named"),
            new Claim(ClaimTypes.Name, "Alice Smith")
        ], "discord"));

        using IServiceScope scope = host.Services.CreateScope();
        ExternalIdentityResolver resolver = scope.ServiceProvider.GetRequiredService<ExternalIdentityResolver>();
        ExternalSignInResult result = await resolver.SignInAsync("discord", principal);

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.SignedIn);
        await Assert.That(await host.Users.GetByUsernameAsync("alice-smith")).IsNotNull();
    }

    [Test]
    public async Task SignInAsync_WithoutAHandleClaim_UsesSubjectFallbackInsteadOfEmail()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "Open");

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "sub-nohandle"),
            new Claim(ClaimTypes.Name, "alice@example.com"),
            new Claim(ClaimTypes.Email, "alice@example.com")
        ], "discord"));

        using IServiceScope scope = host.Services.CreateScope();
        ExternalIdentityResolver resolver = scope.ServiceProvider.GetRequiredService<ExternalIdentityResolver>();
        ExternalSignInResult result = await resolver.SignInAsync("discord", principal);

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.SignedIn);
        await Assert.That(await host.Users.GetByUsernameAsync("user-sub-nohandle")).IsNotNull();
        await Assert.That(await host.Users.GetByUsernameAsync("alice-example.com")).IsNull();
    }

    [Test]
    public async Task SignInAsync_ImportsTheProviderPicture()
    {
        byte[] picture = [0x89, 0x50, 0x4e, 0x47, 0x01, 0x02, 0x03];
        FakeHttpMessageHandler handler = new();
        handler.Enqueue((_, _) => Task.FromResult(ImageResponse(picture, "image/png")));

        await using AuthTestHost host = await AuthTestHost.CreateAsync(
            settings => settings["auth:registrationMode"] = "Open",
            configureServices: services => services.AddSingleton<IHttpClientFactory>(
                new StubHttpClientFactory(handler)));

        ExternalSignInResult result = await SignInAsync(
            host,
            "sub-avatar",
            picture: "https://cdn.discordapp.com/avatars/1/avatar.png");

        await Assert.That(result.Status).IsEqualTo(ExternalSignInStatus.SignedIn);
        StackarrUser? user = await host.Users.GetByUsernameAsync("alice");
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.Avatar).IsEquivalentTo(picture);
        await Assert.That(user.AvatarContentType).IsEqualTo("image/png");
    }

    [Test]
    public async Task LinkAsync_ImportsTheProviderPictureWhenTheAccountHasNone()
    {
        byte[] picture = [0xff, 0xd8, 0xff, 0x01];
        FakeHttpMessageHandler handler = new();
        handler.Enqueue((_, _) => Task.FromResult(ImageResponse(picture, "image/jpeg")));

        await using AuthTestHost host = await AuthTestHost.CreateAsync(
            configureServices: services => services.AddSingleton<IHttpClientFactory>(
                new StubHttpClientFactory(handler)));

        StackarrUser user = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);
        bool linked = await LinkAsync(host, user.Id, "sub-link", "https://cdn.myanimelist.net/images/user.jpg");

        await Assert.That(linked).IsTrue();
        StackarrUser? reloaded = await host.Users.GetByIdAsync(user.Id);
        await Assert.That(reloaded!.Avatar).IsEquivalentTo(picture);
        await Assert.That(reloaded.AvatarContentType).IsEqualTo("image/jpeg");
    }

    static HttpResponseMessage ImageResponse(byte[] bytes, string contentType)
    {
        ByteArrayContent content = new(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
