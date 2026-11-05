using System.Diagnostics;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Services;

namespace Stackarr.Core.Modules;

/// <summary>
/// Global Hangfire server filter that tracks when a background job starts, succeeds, or fails.
/// Registered once, it applies to every recurring and enqueued job. It logs the run, publishes it
/// to the live activity centre while it executes (via <see cref="JobTelemetry"/>), and records an
/// audit event for its start/finish/failure.
///
/// It runs late in the pipeline (<see cref="Order"/>) so it only publishes once the job is about to
/// execute, a job dropped by a concurrency gate such as <see cref="SingleFlightAttribute"/> never
/// reaches it, so no phantom "running" entry is shown.
/// </summary>
public class JobLoggingFilter(JobTelemetry telemetry, ILogger<JobLoggingFilter> logger) : IServerFilter, IJobFilter
{
    const string StopwatchKey = "Stackarr.JobLoggingFilter.Stopwatch";
    const string RunKey = "Stackarr.JobLoggingFilter.Run";

    /// <summary>Run after the concurrency/dedup gates (which use the default order).</summary>
    public int Order => 1000;

    public bool AllowMultiple => true;

    public void OnPerforming(PerformingContext context)
    {
        JobDescription description = Describe(context.BackgroundJob);
        context.Items[StopwatchKey] = Stopwatch.StartNew();
        context.Items[RunKey] = telemetry.Begin(description.Label, context.BackgroundJob.Id);
        logger.LogInformation(
            "Hangfire job starting: {Job} ({JobId})",
            description.Display,
            context.BackgroundJob.Id);
    }

    public void OnPerformed(PerformedContext context)
    {
        JobDescription description = Describe(context.BackgroundJob);
        string jobId = context.BackgroundJob.Id;

        double elapsedSeconds = 0;
        if (context.Items.TryGetValue(StopwatchKey, out object? value) && value is Stopwatch stopwatch)
        {
            stopwatch.Stop();
            elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        }

        if (context.Items.TryGetValue(RunKey, out object? run) && run is JobRun jobRun)
        {
            if (context.Exception is not null)
                jobRun.Fail(elapsedSeconds, context.Exception);
            else
                jobRun.Complete(elapsedSeconds);
        }

        if (context.Exception is not null)
        {
            logger.LogError(
                context.Exception,
                "Hangfire job failed: {Job} ({JobId}) after {Duration:F1}s",
                description.Display,
                jobId,
                elapsedSeconds);
            return;
        }

        logger.LogInformation(
            "Hangfire job finished: {Job} ({JobId}) in {Duration:F1}s",
            description.Display,
            jobId,
            elapsedSeconds);
    }

    /// <summary>A job's short label (for the activity title) and its richer log line.</summary>
    readonly record struct JobDescription(string Label, string Display);

    static JobDescription Describe(BackgroundJob backgroundJob)
    {
        // Prefer the [JobDisplayName] attribute, fall back to "Type.Method".
        InvocationData invocationData = InvocationData.SerializeJob(backgroundJob.Job);
        Job? job = invocationData.DeserializeJob();

        string? displayName = job?.Type
            .GetMethod(job.Method.Name)?
            .GetCustomAttributes(typeof(JobDisplayNameAttribute), inherit: true)
            .OfType<JobDisplayNameAttribute>()
            .FirstOrDefault()?
            .DisplayName;

        string typeName = job?.Type.Name ?? backgroundJob.Job.Type.Name;
        string methodName = job?.Method.Name ?? backgroundJob.Job.Method.Name;
        string qualified = $"{typeName}.{methodName}";

        return string.IsNullOrWhiteSpace(displayName)
            ? new JobDescription(qualified, qualified)
            : new JobDescription(displayName, $"{displayName} [{qualified}]");
    }
}
