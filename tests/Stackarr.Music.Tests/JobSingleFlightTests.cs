using System.Reflection;
using Hangfire;
using Stackarr.Core.Modules;
using Stackarr.Music.Jobs;

namespace Stackarr.Music.Tests;

/// <summary>
/// Verifies that the singleton music jobs drop overlapping invocations (single-flight) rather than
/// queueing them behind a blocking lock and building a backlog.
/// </summary>
public class JobSingleFlightTests
{
    [Test]
    public async Task SingletonJobs_UseSingleFlight_NotBlockingConcurrency()
    {
        Type[] jobs = [typeof(MusicAcquireQueueJob), typeof(MusicDownloadImportJob)];

        foreach (Type job in jobs)
        {
            MethodInfo method = job.GetMethod("Execute")!;

            await Assert.That(method.GetCustomAttribute<SingleFlightAttribute>()).IsNotNull();
            await Assert.That(method.GetCustomAttribute<DisableConcurrentExecutionAttribute>()).IsNull();
        }
    }
}
