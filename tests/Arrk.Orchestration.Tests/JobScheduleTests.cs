using Arrk.Orchestration.Jobs;

namespace Arrk.Orchestration.Tests;

public class JobScheduleTests
{
    [Test]
    public async Task MetricsCollection_RunsEveryTenSeconds()
    {
        await Assert.That(MetricsCollectionJob.DefaultCron).IsEqualTo("*/10 * * * * *");
    }

    [Test]
    public async Task OrphanCleanup_RunsEveryTenMinutes()
    {
        await Assert.That(OrphanCleanupJob.DefaultCron).IsEqualTo("*/10 * * * *");
    }
}
