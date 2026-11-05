using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stackarr.Core.Configuration;
using Stackarr.Core.Services;
using Stackarr.Speedtest;
using Stackarr.Speedtest.Jobs;
using Stackarr.TestSupport;

namespace Stackarr.Speedtest.Tests;

public class SpeedtestModuleTests
{
    static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build();

    static ServiceProvider BuildProvider(MutableOptionsMonitor<SpeedtestConfig>? monitor = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IOptionsMonitor<SpeedtestConfig>>(monitor ?? new MutableOptionsMonitor<SpeedtestConfig>(new SpeedtestConfig()));
        services.AddSingleton<ILogger<SpeedtestModule>>(NullLogger<SpeedtestModule>.Instance);
        return services.BuildServiceProvider();
    }


    [Test]
    public async Task GetServices_IsEmpty()
    {
        await Assert.That(new SpeedtestModule().GetServices()).IsEmpty();
    }

    [Test]
    public async Task ConfigureServices_RegistersTypedHttpClient()
    {
        ServiceCollection services = new();
        services.AddLogging();

        new SpeedtestModule().ConfigureServices(services, Config());
        using ServiceProvider provider = services.BuildServiceProvider();

        CloudflareSpeedtestClient client = provider.GetRequiredService<CloudflareSpeedtestClient>();
        await Assert.That(client).IsNotNull();
    }

    [Test]
    public async Task ConfigureServices_RegistersConnectionHealthProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();

        new SpeedtestModule().ConfigureServices(services, Config());

        await Assert.That(services.Any(d => d.ServiceType == typeof(IConnectionHealthProvider))).IsTrue();
    }

    [Test]
    public async Task ConfigureJobs_RegistersSpeedtestJobs()
    {
        GlobalConfiguration.Configuration.UseInMemoryStorage();
        FakeJobProvider jobs = new();

        new SpeedtestModule().ConfigureJobs(jobs, BuildProvider());

        await Assert.That(jobs.Scheduled.Select(s => s.JobType)).Contains(typeof(SpeedtestJob));
        await Assert.That(jobs.Scheduled.Select(s => s.JobType)).Contains(typeof(VpnSpeedtestJob));
        await Assert.That(jobs.Scheduled.Single(s => s.JobType == typeof(SpeedtestJob)).Cron).IsEqualTo(SpeedtestJob.DefaultCron);
        await Assert.That(jobs.Scheduled.Single(s => s.JobType == typeof(VpnSpeedtestJob)).Cron).IsEqualTo(VpnSpeedtestJob.DefaultCron);
        await Assert.That(jobs.OptionsChangeJobs).Contains(typeof(SpeedtestJob));
        await Assert.That(jobs.OptionsChangeJobs).Contains(typeof(VpnSpeedtestJob));
    }

    // Speed-test schedule is configurable and applied live

    static (FakeJobProvider Jobs, MutableOptionsMonitor<SpeedtestConfig> Monitor) ConfigureJobs(string schedule)
    {
        MutableOptionsMonitor<SpeedtestConfig> monitor = new(new SpeedtestConfig { Schedule = schedule });
        using ServiceProvider provider = BuildProvider(monitor);
        FakeJobProvider jobs = new();
        new SpeedtestModule().ConfigureJobs(jobs, provider);
        return (jobs, monitor);
    }

    [Test]
    public async Task ConfigureJobs_RegistersBothJobs_WithTheConfiguredSchedule()
    {
        (FakeJobProvider jobs, _) = ConfigureJobs("*/15 * * * *");

        await Assert.That(jobs.Scheduled.Count).IsEqualTo(2);
        await Assert.That(jobs.Scheduled.Single(j => j.JobType == typeof(SpeedtestJob)).Cron).IsEqualTo("*/15 * * * *");
        await Assert.That(jobs.Scheduled.Single(j => j.JobType == typeof(VpnSpeedtestJob)).Cron).IsEqualTo("*/15 * * * *");
    }

    [Test]
    public async Task ConfigureJobs_WhenScheduleChanges_ReschedulesBothJobs_Live()
    {
        (FakeJobProvider jobs, MutableOptionsMonitor<SpeedtestConfig> monitor) = ConfigureJobs("*/30 * * * *");

        monitor.Update(new SpeedtestConfig { Schedule = "0 */2 * * *" });

        await Assert.That(jobs.Reschedules.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(jobs.Reschedules[^1].Cron).IsEqualTo("0 */2 * * *");
        await Assert.That(jobs.Scheduled.Single(j => j.JobType == typeof(SpeedtestJob)).Cron).IsEqualTo("0 */2 * * *");
        await Assert.That(jobs.Scheduled.Single(j => j.JobType == typeof(VpnSpeedtestJob)).Cron).IsEqualTo("0 */2 * * *");
    }

    [Test]
    public async Task ConfigureJobs_WhenScheduleBlank_KeepsTheDefaultCron()
    {
        (FakeJobProvider jobs, MutableOptionsMonitor<SpeedtestConfig> monitor) = ConfigureJobs("*/30 * * * *");

        monitor.Update(new SpeedtestConfig { Schedule = "   " });

        await Assert.That(jobs.Scheduled.Single(j => j.JobType == typeof(SpeedtestJob)).Cron).IsEqualTo(SpeedtestJob.DefaultCron);
    }
}
