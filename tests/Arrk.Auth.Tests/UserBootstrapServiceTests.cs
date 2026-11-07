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
        await Assert.That(await host.Users.IsInRoleAsync(admin, ArrkRoles.User)).IsTrue();
        await Assert.That(await host.Users.AuthenticateAsync("admin", AuthTestHost.AdminPassword)).IsNotNull();

        RoleManager<IdentityRole<int>> roles = host.Services.GetRequiredService<RoleManager<IdentityRole<int>>>();
        await Assert.That(await roles.RoleExistsAsync(ArrkRoles.Admin)).IsTrue();
        await Assert.That(await roles.RoleExistsAsync(ArrkRoles.User)).IsTrue();
    }

    [Test]
    public async Task Bootstrap_BackfillsLegacyAdminsOnEveryStartupIdempotently()
    {
        await using TestDatabase database = new();
        string originalStamp;
        await using (AuthTestHost first = await AuthTestHost.CreateAsync(database: database))
        {
            ArrkUser admin = (await first.Users.GetByUsernameAsync("admin"))!;
            ArrkUser otherAdmin = await first.Users.CreateAsync("boss", null, UserRole.Admin);
            await first.Users.SetActiveAsync(otherAdmin.Id, false);
            await first.Users.CreateAsync("normal", null, UserRole.User);
            await first.Users.CreateAsync("pending", null, UserRole.Pending);

            using IServiceScope scope = first.Services.CreateScope();
            UserManager<ArrkUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ArrkUser>>();
            foreach (ArrkUser legacyAdmin in await users.GetUsersInRoleAsync(ArrkRoles.Admin))
            {
                await Assert.That((await users.RemoveFromRoleAsync(legacyAdmin, ArrkRoles.User)).Succeeded).IsTrue();
                await Assert.That(await users.GetRolesAsync(legacyAdmin)).IsEquivalentTo(new[] { ArrkRoles.Admin });
            }

            originalStamp = await first.Users.GetSecurityStampAsync(admin);
        }

        for (int restart = 0; restart < 2; restart++)
        {
            await using AuthTestHost restarted = await AuthTestHost.CreateAsync(database: database);
            foreach (string username in new[] { "admin", "boss" })
            {
                ArrkUser admin = (await restarted.Users.GetByUsernameAsync(username))!;
                await Assert.That(admin.Role).IsEqualTo(UserRole.Admin);
                await Assert.That(await restarted.Users.GetRolesAsync(admin))
                    .IsEquivalentTo(new[] { ArrkRoles.Admin, ArrkRoles.User });
                await Assert.That((await restarted.Users.GetUsersInRoleAsync(ArrkRoles.User)).Any(u => u.Id == admin.Id)).IsTrue();
            }

            ArrkUser initialAdmin = (await restarted.Users.GetByUsernameAsync("admin"))!;
            await Assert.That(await restarted.Users.GetSecurityStampAsync(initialAdmin)).IsEqualTo(originalStamp);
            await Assert.That((await restarted.Users.GetByUsernameAsync("boss"))!.IsActive).IsFalse();
            await Assert.That(await restarted.Users.GetRolesAsync((await restarted.Users.GetByUsernameAsync("normal"))!))
                .IsEquivalentTo(new[] { ArrkRoles.User });
            await Assert.That(await restarted.Users.GetRolesAsync((await restarted.Users.GetByUsernameAsync("pending"))!))
                .IsEquivalentTo(new[] { ArrkRoles.Pending });
            await Assert.That((await restarted.Users.GetAllAsync()).Count).IsEqualTo(4);
        }
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
