using System.Linq.Expressions;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stackarr.Core.Modules;

public class StackarrHangfireJobProvider(
    IRecurringJobManager hangfireRecurringJobs,
    IBackgroundJobClient hangfireBackgroundJobs,
    IServiceProvider services) : IStackarrJobProvider
{
    /// <summary>
    /// Queue served by a dedicated Hangfire worker set, so slow music acquisition jobs (which can be
    /// enqueued by the hundred) never starve the every-minute music import / Soulseek failure check.
    /// </summary>
    public const string MusicQueue = "music";

    /// <summary>
    /// Queue served by its own Hangfire workers for the long (multi-minute) indexer/Soulseek searches.
    /// Keeping these off the default queue stops a backlog of acquisitions from starving the
    /// download-management jobs (slow/stalled detection, storage checks) and the rest of the app.
    /// </summary>
    public const string AcquisitionQueue = "acquisition";

    public IEnumerable<IStackarrJobRecord> Jobs => _jobs.Values;

    readonly Dictionary<string, IStackarrJobRecord> _jobs = [];
    readonly Dictionary<string, ScheduledJob> _scheduled = [];

    sealed record ScheduledJob(Hangfire.Common.Job Job, string? Queue);

    public IStackarrJobProvider RegisterScheduled<T>(
        string jobName,
        Expression<Func<T, Task>> methodCall,
        string cronExpression,
        string? queue = null)
    {
        if (_jobs.TryGetValue(jobName, out IStackarrJobRecord? job))
            throw new ApplicationException($"Job {jobName} already exists with type {job.JobType.Name}");

        if (queue is null)
            hangfireRecurringJobs.AddOrUpdate(jobName, methodCall, cronExpression);
        else
            hangfireRecurringJobs.AddOrUpdate(jobName, methodCall, cronExpression, TimeZoneInfo.Utc, queue);

        _jobs[jobName] = new StackarrJobRecord
        {
            JobName = jobName,
            JobType = typeof(T)
        };
        _scheduled[jobName] = new(Hangfire.Common.Job.FromExpression(methodCall), queue);

        return this;
    }

    public void UpdateSchedule(string jobName, string cronExpression)
    {
        if (!_scheduled.TryGetValue(jobName, out ScheduledJob? scheduled))
            return;

        RecurringJobOptions options = new() { TimeZone = TimeZoneInfo.Utc };
        if (!string.IsNullOrWhiteSpace(scheduled.Queue))
            options.QueueName = scheduled.Queue;

        hangfireRecurringJobs.AddOrUpdate(jobName, scheduled.Job, cronExpression, options);
    }

    public IStackarrJobProvider RegisterOptionsChange<TOptions, TJob>(
        Expression<Func<TJob, Task>> methodCall,
        string? queue = null)
    {
        IOptionsMonitor<TOptions> tvConfigMonitor = services.GetRequiredService<IOptionsMonitor<TOptions>>();
        tvConfigMonitor.OnChange(_ =>
        {
            if (queue is null)
                hangfireBackgroundJobs.Enqueue(methodCall);
            else
                hangfireBackgroundJobs.Enqueue(queue, methodCall);
        });
        return this;
    }
}