using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Auth.Services;

/// <summary>
/// Guarantees the database schema, the built-in roles and a first administrator exist, so a
/// fresh Arrk instance can be signed into immediately. Credentials come from
/// <see cref="AuthConfig"/> and default to <c>admin</c>/<c>admin</c>.
/// </summary>
public sealed class UserBootstrapService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<AuthConfig> config,
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    ILogger<UserBootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not initialise the Arrk database; account bootstrap skipped.");
            return;
        }

        // UserService and the Identity managers are scoped, so resolve them from a scope.
        using IServiceScope scope = scopeFactory.CreateScope();
        UserService userService = scope.ServiceProvider.GetRequiredService<UserService>();
        RoleManager<IdentityRole<int>> roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();

        await EnsureRolesAsync(roles);

        try
        {
            if (await userService.AnyAsync(cancellationToken))
                return;

            AuthConfig cfg = config.CurrentValue;
            ArrkUser admin = await userService.CreateAsync(
                cfg.AdminUsername,
                cfg.AdminPassword,
                UserRole.Admin,
                cfg.AdminUsername,
                cancellationToken);

            logger.LogWarning(
                "Created initial administrator '{Username}'. Sign in and change the password immediately.",
                admin.Username);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create the initial administrator account.");
        }
    }

    async Task EnsureRolesAsync(RoleManager<IdentityRole<int>> roles)
    {
        foreach (string role in new[] { ArrkRoles.Admin, ArrkRoles.User, ArrkRoles.Pending })
        {
            if (await roles.RoleExistsAsync(role))
                continue;

            IdentityResult result = await roles.CreateAsync(new IdentityRole<int>(role));
            if (!result.Succeeded)
                logger.LogError("Could not create role {Role}: {Errors}", role,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
