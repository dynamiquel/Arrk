using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Auth.Abstractions;

namespace Arrk.Auth.Tests;

/// <summary>Verifies the first-run administrator and built-in roles are created automatically.</summary>
public class UserBootstrapServiceTests
{
    [Test]
    public async Task Bootstrap_CreatesAdminAndRoles()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        ArrkUser? admin = await host.Users.GetByUsernameAsync("admin");
        await Assert.That(admin).IsNotNull();
        await Assert.That(admin!.Role).IsEqualTo(UserRole.Admin);
        await Assert.That(await host.Users.IsInRoleAsync(admin, "Admin")).IsTrue();
        await Assert.That(await host.Users.AuthenticateAsync("admin", AuthTestHost.AdminPassword)).IsNotNull();

        RoleManager<IdentityRole<int>> roles = host.Services.GetRequiredService<RoleManager<IdentityRole<int>>>();
        await Assert.That(await roles.RoleExistsAsync(ArrkRoles.Admin)).IsTrue();
        await Assert.That(await roles.RoleExistsAsync(ArrkRoles.User)).IsTrue();
    }

    [Test]
    public async Task Bootstrap_UsesConfiguredCredentials()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
        {
            settings["auth:adminUsername"] = "boss";
            settings["auth:adminPassword"] = "s3cretpass";
        });

        await Assert.That(await host.Users.GetByUsernameAsync("boss")).IsNotNull();
        await Assert.That(await host.Users.AuthenticateAsync("boss", "s3cretpass")).IsNotNull();
    }

    [Test]
    public async Task Bootstrap_UsesTheConfiguredDefaultValueWithoutChangingIt()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
        {
            settings["auth:adminUsername"] = "admin";
            settings["auth:adminPassword"] = "admin";
        });

        await Assert.That(await host.Users.GetByUsernameAsync("admin")).IsNotNull();
        await Assert.That(await host.Users.AuthenticateAsync("admin", "admin")).IsNotNull();
    }
}
