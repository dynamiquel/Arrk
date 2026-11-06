using Microsoft.Extensions.DependencyInjection;
using Arrk.TestSupport;

namespace Arrk.Slskd.Tests;

/// <summary>Covers the jobs the module schedules.</summary>
public class SlskdModuleTests
{
    [Test]
    public async Task ConfigureJobs_RegistersTheFailedTransferCleanup()
    {
        FakeJobProvider jobs = new();
        new SlskdModule().ConfigureJobs(jobs, new ServiceCollection().BuildServiceProvider());

        await Assert.That(jobs.Scheduled.Any(s => s.JobType == typeof(SlskdCleanupJob))).IsTrue();
    }
}
