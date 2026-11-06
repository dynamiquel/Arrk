using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Auth.Services;
using Arrk.Core;

namespace Arrk.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers ASP.NET Core Identity (accounts, roles, external logins) and Arrk's account
    /// services. Requires <c>ArrkDbContext</c> to be registered.
    /// </summary>
    public static IServiceCollection AddArrkIdentity(this IServiceCollection services)
    {
        services.AddIdentityCore<ArrkUser>(options =>
        {
            options.Password.RequiredLength = UserService.MinPasswordLength;
            options.Password.RequiredUniqueChars = 1;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.User.RequireUniqueEmail = false;
            options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-";
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 10;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        })
        .AddRoles<IdentityRole<int>>()
        .AddEntityFrameworkStores<ArrkDbContext>();

        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<ArrkDbContext>>().CreateDbContext());

        services.AddScoped<UserService>();
        services.AddScoped<IUserDirectory>(sp => sp.GetRequiredService<UserService>());
        services.AddScoped<IUserAccountWriter>(sp => sp.GetRequiredService<UserService>());
        services.AddScoped<IExternalIdentityStore>(sp => sp.GetRequiredService<UserService>());
        services.AddScoped<IUserAuthenticator>(sp => sp.GetRequiredService<UserService>());
        services.AddScoped<IUserSessionReader>(sp => sp.GetRequiredService<UserService>());
        services.AddScoped<RegistrationService>();
        services.AddScoped<IExternalIdentityResolver, ExternalIdentityResolver>();

        services.AddHttpClient(ExternalAvatarImporter.ClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(8));
        services.AddSingleton<ExternalAvatarImporter>();

        services.AddHostedService<UserBootstrapService>();

        return services;
    }
}
