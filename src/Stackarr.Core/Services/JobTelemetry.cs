using System.Text;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Events;

namespace Stackarr.Core.Services;

/// <summary>
/// Bridges background job runs into Stackarr's live activity centre and durable event log.
///
/// Concurrent executions of the same job (a recurring job can overlap with itself, and busy jobs
/// like the music acquisition queue run many instances at once) share a single live activity that
/// reports how many are running and clears once they have all finished, so the inbox shows one
/// entry per job, not one per execution.
///
/// Every start/finish/failure is also written to the event log as an audit-only event (no
/// notification) with a topic of the form <c>job.&lt;job-name&gt;.&lt;started|finished|failed&gt;</c>.
/// </summary>
public sealed class JobTelemetry(
    LiveActivityRegistry activity,
    IEventStore events,
    ILogger<JobTelemetry> logger)
{
    public const string StartedEventType = "job.started";
    public const string FinishedEventType = "job.finished";
    public const string FailedEventType = "job.failed";

    readonly Lock _gate = new();
    readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);

    /// <summary>Registers a running execution. Dispose the run (or complete/fail it) when it ends.</summary>
    public JobRun Begin(string label, string jobId)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(label, out Running? running))
            {
                running.Count++;
                running.Scope.Update(ConcurrencyDetail(running.Count));
            }
            else
            {
                LiveActivityScope scope = activity.Begin(LiveActivityKind.Job, $"Running \"{label}\"", ConcurrencyDetail(1));
                _running[label] = new Running(scope);
            }
        }

        Record(StartedEventType, label, jobId, EventSeverity.Info, $"{label} started");
        return new JobRun(this, label, jobId);
    }

    internal void Complete(JobRun run, double elapsedSeconds)
    {
        Record(FinishedEventType, run.Label, run.JobId, EventSeverity.Success, $"{run.Label} finished in {elapsedSeconds:0.0}s");
        Release(run.Label);
    }

    internal void Fail(JobRun run, double elapsedSeconds, Exception error)
    {
        Record(FailedEventType, run.Label, run.JobId, EventSeverity.Error, $"{run.Label} failed after {elapsedSeconds:0.0}s: {error.Message}");
        Release(run.Label);
    }

    /// <summary>Drops one execution from the count, clearing the shared live activity when it reaches zero.</summary>
    internal void Release(string label)
    {
        LiveActivityScope? finished = null;
        lock (_gate)
        {
            if (!_running.TryGetValue(label, out Running? running))
                return;

            running.Count--;
            if (running.Count <= 0)
            {
                finished = running.Scope;
                _running.Remove(label);
            }
            else
            {
                running.Scope.Update(ConcurrencyDetail(running.Count));
            }
        }

        finished?.Dispose();
    }

    static string? ConcurrencyDetail(int count) => count <= 1 ? null : $"{count} running";

    void Record(string eventType, string label, string jobId, EventSeverity severity, string message)
    {
        string phase = eventType[(eventType.LastIndexOf('.') + 1)..];
        _ = RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Job,
            EventType = eventType,
            Topic = $"job.{Slug(label)}.{phase}",
            Severity = severity,
            Message = message,
            Source = jobId,
            SourceName = label
        });
    }

    // The Hangfire filter is synchronous, so recording is fire-and-forget. The helper swallows its
    // own failures (the run itself must never fail because the event log did).
    async Task RecordAsync(SystemEventRecord record)
    {
        try
        {
            await events.RecordAsync(record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record job event {EventType} for {Job}", record.EventType, record.SourceName);
        }
    }

    /// <summary>Lowercase, dash-separated slug of a job label, for use in a hierarchical topic.</summary>
    internal static string Slug(string value)
    {
        StringBuilder sb = new(value.Length);
        bool lastDash = false;
        foreach (char c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        return sb.ToString().TrimEnd('-');
    }

    sealed class Running(LiveActivityScope scope)
    {
        public LiveActivityScope Scope { get; } = scope;
        public int Count { get; set; } = 1;
    }
}

/// <summary>Handle to a running job execution. Completing, failing or disposing it drops one count.</summary>
public sealed class JobRun : IDisposable
{
    readonly JobTelemetry _owner;
    bool _finished;

    internal JobRun(JobTelemetry owner, string label, string jobId)
    {
        _owner = owner;
        Label = label;
        JobId = jobId;
    }

    public string Label { get; }

    public string JobId { get; }

    public void Complete(double elapsedSeconds)
    {
        if (_finished)
            return;
        _finished = true;
        _owner.Complete(this, elapsedSeconds);
    }

    public void Fail(double elapsedSeconds, Exception error)
    {
        if (_finished)
            return;
        _finished = true;
        _owner.Fail(this, elapsedSeconds, error);
    }

    public void Dispose()
    {
        if (_finished)
            return;
        _finished = true;
        _owner.Release(Label);
    }
}
