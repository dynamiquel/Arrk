using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;
using Stackarr.Ui;

namespace Stackarr.Slskd;

/// <summary>
/// Stackarr's Soulseek source. Provisions and drives its own slskd container on Stackarr's network
/// (isolated from any other slskd on the host) and exposes search/download for track-level grabs.
/// </summary>
public class SlskdModule : IStackarrModule
{
    public string ModuleName => "Slskd";
    public string ModuleDescription => "Soulseek (slskd) provisioning and client.";

    public IReadOnlyList<Service> GetServices() => [new(new SlskdServiceDefinition())];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<SlskdConfig>("services:slskd", "Soulseek",
            icon: "MusicNote", group: "Services", order: 75, groupOrder: 20)];

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("slskd", "AGPL-3.0", "slskd", "https://github.com/slskd/slskd", "A modern client-server application for the Soulseek network"),
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlskdConfig>(configuration.GetSection("services:slskd"));

        services.AddSingleton(sp =>
        {
            StackarrInstance stackarr = sp.GetRequiredService<StackarrInstance>();
            IOptionsMonitor<SlskdConfig> config = sp.GetRequiredService<IOptionsMonitor<SlskdConfig>>();
            IConfiguration configuration = sp.GetRequiredService<IConfiguration>();
            // Resolve host per call: toggling VPN routing repoints slskd through Gluetun.
            // When routed through the VPN, slskd shares Gluetun's network namespace, so it is reached
            // through the Gluetun container instead of its own hostname.
            return new SlskdClient(
                () =>
                {
                    string host = configuration.IsSlskdVpnRouted()
                        ? stackarr.GetVpnContainerName()
                        : stackarr.GetContainerName("slskd");
                    return $"http://{host}:5030";
                },
                () =>
                {
                    SlskdConfig current = config.CurrentValue;
                    return (current.ApiUsername, current.ApiPassword);
                });
        });

        services.AddSingleton<SlskdService>();
        services.AddSingleton<SlskdConnectionWatchdog>();
        services.AddScoped<SlskdLimitsJob>();
        services.AddScoped<SlskdCleanupJob>();
        services.AddScoped<SlskdReconnectJob>();
    }

    public void ConfigureJobs(IStackarrJobProvider jobs, IServiceProvider services)
    {
        jobs.RegisterScheduledAndOptionsChange<SlskdLimitsJob, TorrentDownloaderConfig>(
            j => j.Execute(CancellationToken.None),
            SlskdLimitsJob.DefaultCron)
            .RegisterScheduled<SlskdCleanupJob>(
            j => j.Execute(CancellationToken.None),
            SlskdCleanupJob.DefaultCron)
            .RegisterScheduled<SlskdReconnectJob>(
            j => j.Execute(CancellationToken.None),
            SlskdReconnectJob.DefaultCron);
    }
}
