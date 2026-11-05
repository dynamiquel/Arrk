using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Auth.Abstractions;
using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// Exercises <see cref="JellyfinAccountProvisioner"/> against a real Jellyfin container:
/// account creation, authentication, role policy, password reset, rename, disable and delete.
/// This is the proof that Stackarr can create and manage Jellyfin accounts on the user's behalf.
/// </summary>
[NotInParallel(JellyfinServerShared.ParallelKey)]
public class JellyfinAccountProvisionerIntegrationTests
{
    static Task<JellyfinServer?> ServerAsync() => JellyfinServerShared.GetServerAsync();

    static JellyfinAccountProvisioner CreateProvisioner(JellyfinServer server)
        => new(
            server.CreateClient(),
            new MutableOptionsMonitor<JellyfinConfig>(new JellyfinConfig
            {
                ManageUsers = true,
                AdminUsername = server.Config.AdminUsername,
                AdminPassword = server.Config.AdminPassword
            }),
            NullLogger<JellyfinAccountProvisioner>.Instance);

    static string NewUsername() => $"it{Guid.NewGuid():N}"[..12];

    static AccountProvisionRequest Request(string username, string password, bool admin = false, bool active = true, string? externalId = null)
        => new(username, username, password, admin, active, externalId);

    [Test]
    public async Task Provision_NewUser_CreatesAccountThatCanSignIn()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAccountProvisioner provisioner = CreateProvisioner(server);
        string username = NewUsername();
        AccountProvisionResult result = await provisioner.ProvisionAsync(Request(username, "Pass-12345"));

        try
        {
            await Assert.That(result.Created).IsTrue();
            await Assert.That(result.ExternalId).IsNotEmpty();

            JellyfinClient userClient = server.CreateClient(username, "Pass-12345");
            string token = await userClient.AuthenticateAsync();
            await Assert.That(token).IsNotEmpty();
        }
        finally
        {
            await provisioner.RemoveAsync(result.ExternalId);
        }
    }

    [Test]
    public async Task Provision_StandardUser_IsNotAdministrator()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAccountProvisioner provisioner = CreateProvisioner(server);
        string username = NewUsername();
        AccountProvisionResult result = await provisioner.ProvisionAsync(Request(username, "Pass-12345"));

        try
        {
            JellyfinUser user = (await server.CreateClient().GetUsersAsync()).Single(u => u.Name == username);
            await Assert.That(user.Policy?.IsAdministrator).IsFalse();
            await Assert.That(user.Policy?.IsDisabled).IsFalse();
        }
        finally
        {
            await provisioner.RemoveAsync(result.ExternalId);
        }
    }

    [Test]
    public async Task Provision_Admin_IsAdministrator()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAccountProvisioner provisioner = CreateProvisioner(server);
        string username = NewUsername();
        AccountProvisionResult result = await provisioner.ProvisionAsync(Request(username, "Pass-12345", admin: true));

        try
        {
            JellyfinUser user = (await server.CreateClient().GetUsersAsync()).Single(u => u.Name == username);
            await Assert.That(user.Policy?.IsAdministrator).IsTrue();
        }
        finally
        {
            await provisioner.RemoveAsync(result.ExternalId);
        }
    }

    [Test]
    public async Task Provision_Twice_IsIdempotent()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAccountProvisioner provisioner = CreateProvisioner(server);
        string username = NewUsername();
        AccountProvisionResult first = await provisioner.ProvisionAsync(Request(username, "Pass-12345"));
        AccountProvisionResult second = await provisioner.ProvisionAsync(Request(username, "Pass-12345"));

        try
        {
            await Assert.That(second.Created).IsFalse();
            await Assert.That(second.ExternalId).IsEqualTo(first.ExternalId);
            await Assert.That((await server.CreateClient().GetUsersAsync()).Count(u => u.Name == username)).IsEqualTo(1);
        }
        finally
        {
            await provisioner.RemoveAsync(first.ExternalId);
        }
    }

    [Test]
    public async Task Provision_WithNewPassword_ChangesTheJellyfinPassword()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAccountProvisioner provisioner = CreateProvisioner(server);
        string username = NewUsername();
        AccountProvisionResult result = await provisioner.ProvisionAsync(Request(username, "Old-Pass-1"));

        try
        {
            await provisioner.ProvisionAsync(Request(username, "New-Pass-2"));

            JellyfinClient withNew = server.CreateClient(username, "New-Pass-2");
            await Assert.That(await withNew.AuthenticateAsync()).IsNotEmpty();

            JellyfinClient withOld = server.CreateClient(username, "Old-Pass-1");
            await Assert.That(async () => await withOld.AuthenticateAsync()).Throws<HttpRequestException>();
        }
        finally
        {
            await provisioner.RemoveAsync(result.ExternalId);
        }
    }

    [Test]
    public async Task Provision_WithExternalId_RenamesExistingAccount()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAccountProvisioner provisioner = CreateProvisioner(server);
        string original = NewUsername();
        string renamed = NewUsername();
        AccountProvisionResult created = await provisioner.ProvisionAsync(Request(original, "Pass-12345"));

        try
        {
            AccountProvisionResult updated = await provisioner.ProvisionAsync(
                Request(renamed, "Pass-12345", externalId: created.ExternalId));

            await Assert.That(updated.Created).IsFalse();
            await Assert.That(updated.ExternalId).IsEqualTo(created.ExternalId);
            await Assert.That((await server.CreateClient().GetUsersAsync()).Count(u => u.Name == renamed)).IsEqualTo(1);
            await Assert.That((await server.CreateClient().GetUsersAsync()).Any(u => u.Name == original)).IsFalse();

            JellyfinClient renamedClient = server.CreateClient(renamed, "Pass-12345");
            await Assert.That(await renamedClient.AuthenticateAsync()).IsNotEmpty();
        }
        finally
        {
            await provisioner.RemoveAsync(created.ExternalId);
        }
    }

    [Test]
    public async Task Provision_Disabled_CannotSignIn()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAccountProvisioner provisioner = CreateProvisioner(server);
        string username = NewUsername();
        AccountProvisionResult result = await provisioner.ProvisionAsync(Request(username, "Pass-12345", active: false));

        try
        {
            JellyfinUser user = (await server.CreateClient().GetUsersAsync()).Single(u => u.Name == username);
            await Assert.That(user.Policy?.IsDisabled).IsTrue();

            JellyfinClient userClient = server.CreateClient(username, "Pass-12345");
            await Assert.That(async () => await userClient.AuthenticateAsync()).Throws<HttpRequestException>();
        }
        finally
        {
            await provisioner.RemoveAsync(result.ExternalId);
        }
    }

    [Test]
    public async Task Remove_DeletesTheAccount()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAccountProvisioner provisioner = CreateProvisioner(server);
        string username = NewUsername();
        AccountProvisionResult result = await provisioner.ProvisionAsync(Request(username, "Pass-12345"));

        await provisioner.RemoveAsync(result.ExternalId);

        await Assert.That((await server.CreateClient().GetUsersAsync()).Any(u => u.Name == username)).IsFalse();
    }

    [Test]
    public async Task Provision_ExistingJellyfinAdmin_DoesNotDuplicateOrDemote()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAccountProvisioner provisioner = CreateProvisioner(server);
        AccountProvisionResult result = await provisioner.ProvisionAsync(
            Request(JellyfinServer.AdminUsername, "Pass-12345", admin: true));

        List<JellyfinUser> admins = (await server.CreateClient().GetUsersAsync())
            .Where(u => u.Name == JellyfinServer.AdminUsername).ToList();

        await Assert.That(admins.Count).IsEqualTo(1);
        await Assert.That(admins[0].Policy?.IsAdministrator).IsTrue();
        await Assert.That(result.ExternalId).IsEqualTo(admins[0].Id);
    }
}
