using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;

namespace Arrk.Core.Modules;

/// <summary>
/// Server filter that guarantees a job runs at most once at a time by <b>dropping</b> (canceling)
/// any invocation that finds another run already in progress, rather than queueing it behind a lock.
///
/// Hangfire's <see cref="DisableConcurrentExecutionAttribute"/> also enforces exclusivity, but a
/// duplicate blocks for the whole timeout and then fails (and is retried), so a frequently enqueued
/// job piles up a backlog of blocked instances that do no useful work. Arrk's jobs re-evaluate
/// persistent work on every run, so dropping a duplicate is the correct behaviour and keeps the
/// queue empty.
///
/// Use only on singleton-ish jobs. Never on per-item jobs that must run concurrently (e.g. one job
/// invocation per track).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SingleFlightAttribute : JobFilterAttribute, IServerFilter
{
    const string LockKey = "Arrk.SingleFlight.Lock";

    public void OnPerforming(PerformingContext context)
    {
        string resource = BuildResource(context.BackgroundJob.Job);

        try
        {
            // Non-blocking: if a run is already in progress, give up immediately instead of waiting.
            context.Items[LockKey] = context.Connection.AcquireDistributedLock(resource, TimeSpan.Zero);
        }
        catch (DistributedLockTimeoutException)
        {
            // A canceled job is moved to the Deleted state by Hangfire: it neither executes nor retries.
            context.Canceled = true;
        }
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(LockKey, out object? value) && value is IDisposable held)
        {
            held.Dispose();
            context.Items.Remove(LockKey);
        }
    }

    static string BuildResource(Job job) =>
        $"arrk:single-flight:{job.Type.FullName}.{job.Method.Name}";
}
