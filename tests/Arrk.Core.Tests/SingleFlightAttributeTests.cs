using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Arrk.Core.Modules;

namespace Arrk.Core.Tests;

/// <summary>
/// Covers single-flight: when another run already holds the lock the invocation is canceled (dropped),
/// not queued or failed, and a successful acquisition releases its lock when the run finishes.
/// </summary>
public class SingleFlightAttributeTests
{
    [Test]
    public async Task OnPerforming_WhenAnotherRunHoldsTheLock_CancelsInsteadOfFailing()
    {
        FakeConnection connection = new() { AcquireFails = true };
        PerformingContext context = BuildContext(connection);

        new SingleFlightAttribute().OnPerforming(context);

        await Assert.That(context.Canceled).IsTrue();
    }

    [Test]
    public async Task OnPerforming_WhenTheLockIsFree_AcquiresIt_AndOnPerformedReleasesIt()
    {
        FakeConnection connection = new();
        SingleFlightAttribute filter = new();
        PerformingContext context = BuildContext(connection);

        filter.OnPerforming(context);

        await Assert.That(context.Canceled).IsFalse();
        await Assert.That(connection.LastResource).Contains("single-flight");

        filter.OnPerformed(new PerformedContext(context, null, false, null));

        await Assert.That(connection.Released).IsTrue();
    }

    [Test]
    public async Task TheLockResource_IsStableForAJob()
    {
        FakeConnection first = new();
        FakeConnection second = new();
        SingleFlightAttribute filter = new();

        filter.OnPerforming(BuildContext(first));
        filter.OnPerforming(BuildContext(second));

        await Assert.That(first.LastResource).IsEqualTo(second.LastResource);
    }

    static PerformingContext BuildContext(FakeConnection connection)
    {
        MethodInfo method = typeof(FakeJob).GetMethod(nameof(FakeJob.Execute))!;
        Job job = new(typeof(FakeJob), method);
        BackgroundJob backgroundJob = new("1", job, DateTime.UtcNow);
        PerformContext performContext = new(null, connection, backgroundJob, new FakeCancellationToken());
        return new PerformingContext(performContext);
    }

    sealed class FakeCancellationToken : IJobCancellationToken
    {
        public CancellationToken ShutdownToken => CancellationToken.None;

        public void ThrowIfCancellationRequested()
        {
        }
    }

    public static class FakeJob
    {
        public static void Execute()
        {
        }
    }

    sealed class FakeConnection : IStorageConnection
    {
        public bool AcquireFails { get; init; }

        public string? LastResource { get; private set; }

        public bool Released { get; private set; }

        public IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
        {
            LastResource = resource;
            if (AcquireFails)
                throw new DistributedLockTimeoutException(resource);

            return new Releasable(() => Released = true);
        }

        public void Dispose()
        {
        }

        public IWriteOnlyTransaction CreateWriteTransaction() => throw new NotSupportedException();
        public string CreateExpiredJob(Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn) => throw new NotSupportedException();
        public IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void SetJobParameter(string id, string name, string value) => throw new NotSupportedException();
        public string GetJobParameter(string id, string name) => throw new NotSupportedException();
        public JobData? GetJobData(string jobId) => throw new NotSupportedException();
        public StateData? GetStateData(string jobId) => throw new NotSupportedException();
        public void AnnounceServer(string serverId, ServerContext context) => throw new NotSupportedException();
        public void RemoveServer(string serverId) => throw new NotSupportedException();
        public void Heartbeat(string serverId) => throw new NotSupportedException();
        public int RemoveTimedOutServers(TimeSpan timeOut) => throw new NotSupportedException();
        public HashSet<string> GetAllItemsFromSet(string key) => throw new NotSupportedException();
        public string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore) => throw new NotSupportedException();
        public void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs) => throw new NotSupportedException();
        public Dictionary<string, string>? GetAllEntriesFromHash(string key) => throw new NotSupportedException();

        sealed class Releasable(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}
