using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Stackarr.Auth.Abstractions;
using Stackarr.Auth.Components.Shared;
using Stackarr.Auth.Services;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Modules;
using Stackarr.Ui;

namespace Stackarr.Auth;

/// <summary>
/// Authentication module: accounts, roles, external identity linking, the OpenIddict provider and
/// the account/users UI. The shell only composes its regions and pages through the shared contracts.
/// </summary>
public sealed class AuthModule : IStackarrModule
{
    public string ModuleName => "Auth";
    public string ModuleDescription => "Accounts, sign-in, OIDC and the account/users UI.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<AuthConfig>(AuthConfig.SectionName, "Authentication",
            icon: "Security", group: "Infrastructure", order: 8, groupOrder: 10)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthConfig>(configuration.GetSection(AuthConfig.SectionName));

        AuthConfig auth = configuration.GetSection(AuthConfig.SectionName).Get<AuthConfig>() ?? new();
        RoutesConfig routes = configuration.GetSection(RoutesConfig.SectionName).Get<RoutesConfig>() ?? new();
        string configDirectory = StackarrFiles.ConfigDirectory(DataRoute.Root);

        services.AddStackarrAuthentication(configuration);
        services.AddStackarrIdentity();

        // Data Protection keys persist the auth cookie and OIDC handshake state across restarts.
        string dataProtectionDirectory = Path.Combine(configDirectory, "dataprotection");
        Directory.CreateDirectory(dataProtectionDirectory);
        services.AddDataProtection()
            .SetApplicationName("stackarr")
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));

        services.AddStackarrOpenIddict(auth, routes, configDirectory);

        services.AddSingleton<LoginProviderRegistry>();
        services.AddSingleton<IOidcProviderInfo, OidcProviderInfo>();

        services.AddSingleton<INavigationProvider, AuthNavigation>();
        services.AddSingleton<IUiSlotProvider, AuthShellSlots>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapStackarrAuthEndpoints();
        endpoints.MapStackarrOpenIddictEndpoints();
    }
}
