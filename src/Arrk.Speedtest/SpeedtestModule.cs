using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Notifications;
using Arrk.Core.Services;
using Arrk.Speedtest.Jobs;
using Arrk.Ui;

namespace Arrk.Speedtest;

public class SpeedtestModule : IArrkModule
{
    public string ModuleName => "Speedtest";
    public string ModuleDescription => "Network and VPN speedtests with a connection-health verdict.";

    public IReadOnlyList<Core.Services.Service> GetServices() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<CloudflareSpeedtestClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
        });
        services.Configure<Core.Configuration.SpeedtestConfig>(configuration.GetSection("monitoring:speedtest"));
        services.AddSingleton<VpnSpeedtestClient>();
        services.AddSingleton<IConnectionHealthProvider, SpeedtestConnectionHealthProvider>();
        services.AddSingleton<INotificationEventProvider, SpeedtestNotificationEvents>();
        services.AddSingleton<ITileProvider, SpeedtestTileProvider>();
    }

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services)
    {
        IOptionsMonitor<SpeedtestConfig> config =
            services.GetRequiredService<IOptionsMonitor<SpeedtestConfig>>();
        ILogger<SpeedtestModule> logger = services.GetRequiredService<ILogger<SpeedtestModule>>();

        // Both speed-test jobs share monitoring.speedtest.schedule. Register with the safe default
        // first, then apply the configured schedule (and re-apply on every change) so editing it
        // takes effect without a restart.
        jobs.RegisterScheduledAndOptionsChange<SpeedtestJob, SpeedtestConfig>(
            j => j.Execute(CancellationToken.None),
            SpeedtestJob.DefaultCron);
        jobs.RegisterScheduledAndOptionsChange<VpnSpeedtestJob, SpeedtestConfig>(
            j => j.Execute(CancellationToken.None),
            VpnSpeedtestJob.DefaultCron);

        ApplySchedule(config.CurrentValue.Schedule);
        config.OnChange(current => ApplySchedule(current.Schedule));
        return;

        void ApplySchedule(string? schedule)
        {
            if (string.IsNullOrWhiteSpace(schedule))
                return;

            try
            {
                jobs.UpdateSchedule<SpeedtestJob>(schedule);
                jobs.UpdateSchedule<VpnSpeedtestJob>(schedule);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Invalid monitoring.speedtest.schedule '{Schedule}'; keeping the previous schedule", schedule);
            }
        }
    }
}
