using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Events;
using Arrk.Notifications;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.TestSupport;

namespace Arrk.Core.Tests;

/// <summary>
/// Covers job telemetry: concurrent executions of the same job share one live activity (cleared once
/// they have all finished), and every start/finish/failure is recorded as an audit-only event
/// (category "Job", no notification) with a <c>job.&lt;name&gt;.&lt;phase&gt;</c> topic.
/// </summary>
public class JobTelemetryTests
{
    [Test]
    public async Task Begin_PublishesOneLiveActivity_AndRecordsAStartedEventWithTopic()
    {
        LiveActivityRegistry activity = new();
        FakeEventStore events = new();
        JobTelemetry telemetry = new(activity, events, NullLogger<JobTelemetry>.Instance);

        using JobRun run = telemetry.Begin("Music Acquire Queue", "42");

        await Assert.That(activity.Count).IsEqualTo(1);
        LiveActivityEntry entry = activity.Active.Single();
        await Assert.That(entry.Kind).IsEqualTo(LiveActivityKind.Job);
        await Assert.That(entry.Title).Contains("Music Acquire Queue");

        SystemEventRecord record = events.Records.Single();
        await Assert.That(record.Category).IsEqualTo(EventCategory.Job);
        await Assert.That(record.EventType).IsEqualTo(JobTelemetry.StartedEventType);
        await Assert.That(record.Topic).IsEqualTo("job.music-acquire-queue.started");
        await Assert.That(record.Severity).IsEqualTo(EventSeverity.Info);
        await Assert.That(record.SourceName).IsEqualTo("Music Acquire Queue");
        await Assert.That(record.Source).IsEqualTo("42");
        await Assert.That(record.Notification).IsNull();
    }

    [Test]
    public async Task ConcurrentRunsOfTheSameJob_ShareASingleActivity()
    {
        LiveActivityRegistry activity = new();
        FakeEventStore events = new();
        JobTelemetry telemetry = new(activity, events, NullLogger<JobTelemetry>.Instance);

        JobRun first = telemetry.Begin("Music Acquire Queue", "1");
        JobRun second = telemetry.Begin("Music Acquire Queue", "2");
        JobRun third = telemetry.Begin("Music Acquire Queue", "3");

        // One entry, no matter how many executions overlap, reporting the concurrency.
        await Assert.That(activity.Count).IsEqualTo(1);
        await Assert.That(activity.Active.Single().Detail).IsEqualTo("3 running");

        first.Complete(1.0);
        second.Complete(1.0);
        await Assert.That(activity.Count).IsEqualTo(1); // one still running
        await Assert.That(activity.Active.Single().Detail).IsNull(); // single run: no count shown

        third.Complete(1.0);
        await Assert.That(activity.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Complete_RecordsSuccess_AndClearsTheActivity()
    {
        LiveActivityRegistry activity = new();
        FakeEventStore events = new();
        JobTelemetry telemetry = new(activity, events, NullLogger<JobTelemetry>.Instance);

        JobRun run = telemetry.Begin("Config Sync", "7");
        run.Complete(1.25);

        await Assert.That(activity.Count).IsEqualTo(0);
        SystemEventRecord record = events.Records.Last();
        await Assert.That(record.EventType).IsEqualTo(JobTelemetry.FinishedEventType);
        await Assert.That(record.Topic).IsEqualTo("job.config-sync.finished");
        await Assert.That(record.Severity).IsEqualTo(EventSeverity.Success);
        await Assert.That(record.Message).Contains("finished");
    }

    [Test]
    public async Task Fail_RecordsError_AndClearsTheActivity()
    {
        LiveActivityRegistry activity = new();
        FakeEventStore events = new();
        JobTelemetry telemetry = new(activity, events, NullLogger<JobTelemetry>.Instance);

        JobRun run = telemetry.Begin("Speed Test", "9");
        run.Fail(0.5, new InvalidOperationException("boom"));

        await Assert.That(activity.Count).IsEqualTo(0);
        SystemEventRecord record = events.Records.Last();
        await Assert.That(record.EventType).IsEqualTo(JobTelemetry.FailedEventType);
        await Assert.That(record.Topic).IsEqualTo("job.speed-test.failed");
        await Assert.That(record.Severity).IsEqualTo(EventSeverity.Error);
        await Assert.That(record.Message).Contains("boom");
        await Assert.That(record.Notification).IsNull();
    }

    [Test]
    public async Task Dispose_WithoutCompleting_ClearsTheActivityWithoutRecording()
    {
        LiveActivityRegistry activity = new();
        FakeEventStore events = new();
        JobTelemetry telemetry = new(activity, events, NullLogger<JobTelemetry>.Instance);

        JobRun run = telemetry.Begin("Status Updates", "1");
        run.Dispose();

        await Assert.That(activity.Count).IsEqualTo(0);
        await Assert.That(events.Records.Count).IsEqualTo(1); // started only
    }

    [Test]
    public async Task Dispose_AfterCompletion_DoesNotRecordAgain()
    {
        LiveActivityRegistry activity = new();
        FakeEventStore events = new();
        JobTelemetry telemetry = new(activity, events, NullLogger<JobTelemetry>.Instance);

        JobRun run = telemetry.Begin("Status Updates", "1");
        run.Complete(0.1);
        run.Dispose();

        await Assert.That(events.Records.Count).IsEqualTo(2); // started + finished only
        await Assert.That(activity.Count).IsEqualTo(0);
    }

}
