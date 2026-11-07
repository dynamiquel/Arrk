using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Arrk.Auth.Abstractions;
using Arrk.Auth.Components.Shared;
using Arrk.Auth.Services;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Documentation;
using Arrk.Core.Models;
using Arrk.Core.Modules;
using Arrk.Ui;

namespace Arrk.Auth;

/// <summary>
/// Authentication module: accounts, roles, external identity linking, the OpenIddict provider and
/// the account/users UI. The shell only composes its regions and pages through the shared contracts.
/// </summary>
public sealed class AuthModule : IArrkModule
{
    public string ModuleName => "Auth";
    public string ModuleDescription => "Accounts, sign-in, OIDC and the account/users UI.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<AuthConfig>(AuthConfig.SectionName, "Authentication",
            icon: "Security", group: "Infrastructure", order: 8, groupOrder: 10)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDocumentation("auth", typeof(AuthModule).Assembly);
        services.Configure<AuthConfig>(configuration.GetSection(AuthConfig.SectionName));

        AuthConfig auth = configuration.GetSection(AuthConfig.SectionName).Get<AuthConfig>() ?? new();
        RoutesConfig routes = configuration.GetSection(RoutesConfig.SectionName).Get<RoutesConfig>() ?? new();
        string configDirectory = ArrkFiles.ConfigDirectory(DataRoute.Root);

        services.AddArrkAuthentication(configuration);
        services.AddArrkIdentity();

        // Data Protection keys persist the auth cookie and OIDC handshake state across restarts.
        string dataProtectionDirectory = Path.Combine(configDirectory, "dataprotection");
        Directory.CreateDirectory(dataProtectionDirectory);
        services.AddDataProtection()
            // Keep the discriminator used to encrypt persisted auth state (including MAL tokens).
            .SetApplicationName("stackarr")
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));

        services.AddArrkOpenIddict(auth, routes, configDirectory);

        services.AddSingleton<LoginProviderRegistry>();
        services.AddSingleton<IOidcProviderInfo, OidcProviderInfo>();

        services.AddSingleton<INavigationProvider, AuthNavigation>();
        services.AddSingleton<IUiSlotProvider, AuthShellSlots>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapArrkAuthEndpoints();
        endpoints.MapArrkOpenIddictEndpoints();
    }
}
