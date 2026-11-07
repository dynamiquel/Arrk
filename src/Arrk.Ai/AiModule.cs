using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Core.Services;
using Arrk.Ui;

namespace Arrk.Ai;

/// <summary>
/// The optional AI module. It owns the <c>ai</c> config section, the provider-agnostic client,
/// usage accounting and (in later slices) the assistant surface. Domain modules contribute their
/// own AI behaviour through contracts in this assembly; this module references none of them.
/// </summary>
public sealed class AiModule : IArrkModule
{
    public const string ModuleNameValue = "AI";

    public string ModuleName => ModuleNameValue;

    public string ModuleDescription =>
        "AI assistant, release ranking and usage accounting for cloud or local models.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
    [
        new ConfigSectionProvider<AiConfig>(AiConfig.SectionName, "AI",
            icon: "AutoAwesome", group: "Infrastructure", order: 70, groupOrder: 10)
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddArrkAi(configuration);
        services.AddSingleton<AiMaintenanceJob>();
        services.AddSingleton<INavigationProvider, AiNavigation>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapArrkMcp();

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services)
    {
        jobs.RegisterScheduled<AiMaintenanceJob>(
            job => job.Execute(CancellationToken.None),
            AiMaintenanceJob.DefaultCron);
    }
}
