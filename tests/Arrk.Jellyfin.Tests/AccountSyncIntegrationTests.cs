using Arrk.Auth;
using Arrk.Auth.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Auth.Abstractions;
using Arrk.TestSupport;

namespace Arrk.Jellyfin.Tests;

/// <summary>
/// The golden end-to-end path: an Arrk account (real Identity + SQLite) is mirrored into a real
/// Jellyfin container, and role/password/rename/disable/delete changes all propagate. This proves
/// one set of credentials works in both Arrk and Jellyfin clients (including Infuse).
/// </summary>
[NotInParallel(JellyfinServerShared.ParallelKey)]
public class AccountSyncIntegrationTests
{
    static Task<JellyfinServer?> ServerAsync() => JellyfinServerShared.GetServerAsync();

    [Test]
    public async Task ArrkAccount_IsProvisionedAndKeptInSyncWithJellyfin()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        await using AccountTestDatabase db = new();

        JellyfinAccountProvisioner provisioner = new(
            server.CreateClient(),
            new MutableOptionsMonitor<JellyfinConfig>(new JellyfinConfig
            {
                ManageUsers = true,
                AdminUsername = server.Config.AdminUsername,
                AdminPassword = server.Config.AdminPassword
            }),
            NullLogger<JellyfinAccountProvisioner>.Instance);

        ServiceCollection services = new();
        services.AddLogging();
        services.AddOptions();
        services.AddDbContextFactory<ArrkDbContext>(o => o.UseSqlite(db.ConnectionString).ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<IOptionsMonitor<AuthConfig>>(new MutableOptionsMonitor<AuthConfig>(new AuthConfig
        {
            Players = new PlayerProvisioningConfig { ProvisionAccounts = true, ProvisionPasswords = true }
        }));
        services.AddSingleton<IAccountProvisioner>(provisioner);
        services.AddArrkIdentity();

        await using ServiceProvider provider = services.BuildServiceProvider();

        // Migrate + create the built-in roles (normally done by UserBootstrapService).
        IDbContextFactory<ArrkDbContext> factory = provider.GetRequiredService<IDbContextFactory<ArrkDbContext>>();
        await using (ArrkDbContext context = await factory.CreateDbContextAsync())
            await context.Database.MigrateAsync();

        RoleManager<IdentityRole<int>> roleManager = provider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        foreach (string role in new[] { ArrkRoles.Admin, ArrkRoles.User })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole<int>(role));
        }

        UserService users = provider.GetRequiredService<UserService>();

        string username = UniqueName();
        ArrkUser user = await users.CreateAsync(username, "E2E-Pass-1", UserRole.User, username);

        // Created in Arrk => created in Jellyfin with the same credentials.
        await using (ArrkDbContext context = await factory.CreateDbContextAsync())
        {
            Arrk.Auth.Abstractions.UserProviderLink? link = await context.UserProviderLinks
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.UserId == user.Id && l.Provider == "jellyfin", CancellationToken.None);
            await Assert.That(link).IsNotNull();
            await Assert.That(link!.ExternalId).IsNotEmpty();
        }

        JellyfinClient adminClient = server.CreateClient();
        await Assert.That(await server.CreateClient(username, "E2E-Pass-1").AuthenticateAsync()).IsNotEmpty();

        // Role changes propagate.
        await users.SetRoleAsync(user.Id, UserRole.Admin);
        JellyfinUser jellyfinUser = (await adminClient.GetUsersAsync()).Single(u => u.Name == username);
        await Assert.That(jellyfinUser.Policy?.IsAdministrator).IsTrue();

        // Password changes propagate.
        await users.ResetPasswordAsync(user.Id, "E2E-Pass-2");
        await Assert.That(await server.CreateClient(username, "E2E-Pass-2").AuthenticateAsync()).IsNotEmpty();
        await Assert.That(async () => await server.CreateClient(username, "E2E-Pass-1").AuthenticateAsync())
            .Throws<HttpRequestException>();

        // Username changes rename the Jellyfin account rather than duplicating it.
        string renamed = UniqueName();
        await users.UpdateProfileAsync(user.Id, renamed, renamed);
        List<JellyfinUser> afterRename = await adminClient.GetUsersAsync();
        await Assert.That(afterRename.Any(u => u.Name == renamed)).IsTrue();
        await Assert.That(afterRename.Any(u => u.Name == username)).IsFalse();
        await Assert.That(await server.CreateClient(renamed, "E2E-Pass-2").AuthenticateAsync()).IsNotEmpty();

        // Demote back to a standard user: Jellyfin refuses to disable administrators.
        await users.SetRoleAsync(user.Id, UserRole.User);
        JellyfinUser demoted = (await adminClient.GetUsersAsync()).Single(u => u.Name == renamed);
        await Assert.That(demoted.Policy?.IsAdministrator).IsFalse();

        // Disabling propagates.
        await users.SetActiveAsync(user.Id, false);
        JellyfinUser disabled = (await adminClient.GetUsersAsync()).Single(u => u.Name == renamed);
        await Assert.That(disabled.Policy?.IsDisabled).IsTrue();
        await Assert.That(async () => await server.CreateClient(renamed, "E2E-Pass-2").AuthenticateAsync())
            .Throws<HttpRequestException>();

        // Deleting removes the Jellyfin account.
        await users.DeleteAsync(user.Id);
        await Assert.That((await adminClient.GetUsersAsync()).Any(u => u.Name == renamed)).IsFalse();
    }

    [Test]
    public async Task PasswordProvisioning_CanBeDisabled_IndependentlyOfLogin()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        await using AccountTestDatabase db = new();

        JellyfinAccountProvisioner provisioner = new(
            server.CreateClient(),
            new MutableOptionsMonitor<JellyfinConfig>(new JellyfinConfig
            {
                ManageUsers = true,
                AdminUsername = server.Config.AdminUsername,
                AdminPassword = server.Config.AdminPassword
            }),
            NullLogger<JellyfinAccountProvisioner>.Instance);

        ServiceCollection services = new();
        services.AddLogging();
        services.AddOptions();
        services.AddDbContextFactory<ArrkDbContext>(o => o.UseSqlite(db.ConnectionString).ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<IOptionsMonitor<AuthConfig>>(new MutableOptionsMonitor<AuthConfig>(new AuthConfig
        {
            Players = new PlayerProvisioningConfig { ProvisionAccounts = true, ProvisionPasswords = false }
        }));
        services.AddSingleton<IAccountProvisioner>(provisioner);
        services.AddArrkIdentity();

        await using ServiceProvider provider = services.BuildServiceProvider();
        IDbContextFactory<ArrkDbContext> factory = provider.GetRequiredService<IDbContextFactory<ArrkDbContext>>();
        await using (ArrkDbContext context = await factory.CreateDbContextAsync())
            await context.Database.MigrateAsync();

        RoleManager<IdentityRole<int>> roleManager = provider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        foreach (string role in new[] { ArrkRoles.Admin, ArrkRoles.User })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole<int>(role));
        }

        UserService users = provider.GetRequiredService<UserService>();
        string username = UniqueName();
        ArrkUser user = await users.CreateAsync(username, "NoSync-Pass", UserRole.User, username);

        try
        {
            // Account exists in Jellyfin, but the password was not forwarded, so it cannot be
            // used to sign in there.
            List<JellyfinUser> jellyfinUsers = await server.CreateClient().GetUsersAsync();
            await Assert.That(jellyfinUsers.Any(u => u.Name == username)).IsTrue();
            await Assert.That(async () => await server.CreateClient(username, "NoSync-Pass").AuthenticateAsync())
                .Throws<HttpRequestException>();
        }
        finally
        {
            ArrkUser? linkUser = await users.GetByIdAsync(user.Id);
            _ = linkUser;
            await users.DeleteAsync(user.Id);
        }
    }

    static string UniqueName() => $"e2e{Guid.NewGuid():N}"[..12];
}
