using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Arrk.Auth.Services;
using Arrk.Auth.Abstractions;

namespace Arrk.App.Tests;

/// <summary>
/// Verifies that the cookie authentication options follow live <c>auth</c> config edits
/// (<c>require_https</c>, <c>session_days</c>) without an app restart.
/// </summary>
public class AuthCookieConfigLiveTests
{
    [Test]
    [NotInParallel]
    public async Task CookieOptions_FollowLiveConfigFileChange()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-auth-cookie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "arrk.yaml");
        await File.WriteAllTextAsync(path, "auth:\n  sessionDays: 7\n");

        try
        {
            ConfigurationBuilder builder = new();
            builder.AddYamlFile(path, optional: false, reloadOnChange: true);
            IConfiguration configuration = builder.Build();

            ServiceCollection services = new();
            services.AddLogging();
            services.Configure<AuthConfig>(configuration.GetSection("auth"));
            services.AddArrkAuthentication(configuration);
            using ServiceProvider provider = services.BuildServiceProvider();

            IOptionsMonitor<CookieAuthenticationOptions> monitor =
                provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
            string scheme = CookieAuthenticationDefaults.AuthenticationScheme;

            await Assert.That(monitor.Get(scheme).ExpireTimeSpan.TotalDays).IsEqualTo(7);

            // A real config file edit (what the config editor/apply writes) must rebuild the options.
            // Filesystem watchers can coalesce rapid writes, so rewrite until the reload is observed.
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (monitor.Get(scheme).ExpireTimeSpan.TotalDays != 3 && DateTime.UtcNow < deadline)
            {
                await File.WriteAllTextAsync(path, "auth:\n  sessionDays: 3\n");
                await Task.Delay(500);
            }

            await Assert.That(monitor.Get(scheme).ExpireTimeSpan.TotalDays).IsEqualTo(3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    [NotInParallel]
    public async Task DiscordOidcOptions_FollowLiveConfigFileChange()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-auth-discord-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "arrk.yaml");
        await File.WriteAllTextAsync(path,
            "auth:\n  external:\n    discord:\n      enabled: false\n");

        try
        {
            ConfigurationBuilder builder = new();
            builder.AddYamlFile(path, optional: false, reloadOnChange: true);
            IConfiguration configuration = builder.Build();

            ServiceCollection services = new();
            services.AddLogging();
            services.Configure<AuthConfig>(configuration.GetSection("auth"));
            services.AddArrkAuthentication(configuration);
            using ServiceProvider provider = services.BuildServiceProvider();

            IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions> monitor =
                provider.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>();

            await Assert.That(monitor.Get(ArrkAuthenticationExtensions.DiscordScheme).ClientId)
                .IsEqualTo("arrk-discord");

            // Enabling Discord and supplying a client id live must be reflected in the OIDC options.
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (monitor.Get(ArrkAuthenticationExtensions.DiscordScheme).ClientId != "discord-client"
                && DateTime.UtcNow < deadline)
            {
                await File.WriteAllTextAsync(path,
                    "auth:\n  external:\n    discord:\n      enabled: true\n      clientId: discord-client\n      clientSecret: s3cret\n");
                await Task.Delay(500);
            }

            Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions options =
                monitor.Get(ArrkAuthenticationExtensions.DiscordScheme);
            await Assert.That(options.ClientId).IsEqualTo("discord-client");
            await Assert.That(options.ClientSecret).IsEqualTo("s3cret");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
