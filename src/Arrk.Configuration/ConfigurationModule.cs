using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Modules;
using Arrk.Core.Services;
using Arrk.Ui;

namespace Arrk.Configuration;

/// <summary>
/// Configuration is the module that owns the config model's editors: validation, the generated JSON
/// schema/autocomplete and the in-app config editor page. It contributes its navigation, script and
/// the <see cref="ConfigEditorService"/> to the shell through the <c>Arrk.Ui</c> contracts.
/// </summary>
public class ConfigurationModule : IArrkModule
{
    public string ModuleName => "Configuration";
    public string ModuleDescription => "Config validation, schema/autocomplete and the in-app config editor.";

    public IReadOnlyList<Service> GetServices() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INavigationProvider, ConfigurationNavigation>();
        services.AddSingleton<IHeadAssetProvider, ConfigurationHeadAssets>();

        // The editor reads/writes the real arrk.yaml under the data route and keeps version
        // backups beside it.
        services.AddSingleton(sp => new ConfigEditorService(
            ArrkFiles.ConfigDirectory(DataRoute.Root),
            sp.GetRequiredService<ConfigSectionRegistry>(),
            sp,
            sp.GetRequiredService<ILogger<ConfigEditorService>>()));
    }
}
