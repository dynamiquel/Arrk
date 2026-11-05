using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;
using Stackarr.Ui;

namespace Stackarr.Ai;

/// <summary>
/// The optional AI module. It owns the <c>ai</c> config section, the provider-agnostic client,
/// usage accounting and (in later slices) the assistant surface. Domain modules contribute their
/// own AI behaviour through contracts in this assembly; this module references none of them.
/// </summary>
public sealed class AiModule : IStackarrModule
{
    public const string ModuleNameValue = "AI";

    public string ModuleName => ModuleNameValue;

    public string ModuleDescription =>
        "Optional, provider-agnostic AI: assistant, release ranking, usage accounting.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
    [
        new ConfigSectionProvider<AiConfig>(AiConfig.SectionName, "AI",
            icon: "AutoAwesome", group: "Infrastructure", order: 70, groupOrder: 10)
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddStackarrAi(configuration);
        services.AddSingleton<AiMaintenanceJob>();
        services.AddSingleton<INavigationProvider, AiNavigation>();
    }

    public void ConfigureJobs(IStackarrJobProvider jobs, IServiceProvider services)
    {
        jobs.RegisterScheduled<AiMaintenanceJob>(
            job => job.Execute(CancellationToken.None),
            AiMaintenanceJob.DefaultCron);
    }
}
