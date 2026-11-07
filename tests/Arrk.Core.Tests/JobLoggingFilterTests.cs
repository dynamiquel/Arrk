using Hangfire.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Modules;
using Arrk.Core.Services;

namespace Arrk.Core.Tests;

/// <summary>
/// Covers the ordering contract that keeps the live-activity feed honest: the job-logging filter runs
/// after concurrency gates, so it never publishes an activity for an invocation that gets dropped.
/// </summary>
public class JobLoggingFilterTests
{
    [Test]
    public async Task RunsAfterConcurrencyGates()
    {
        JobLoggingFilter filter = new(
            new JobTelemetry(new LiveActivityRegistry(), new NullEventStore(), NullLogger<JobTelemetry>.Instance),
            NullLogger<JobLoggingFilter>.Instance);

        IJobFilter jobFilter = filter;

        await Assert.That(jobFilter.Order).IsGreaterThan(JobFilter.DefaultOrder);
        await Assert.That(jobFilter.AllowMultiple).IsTrue();
    }

    sealed class NullEventStore : IEventStore
    {
        public Task RecordAsync(SystemEventRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordAsync(IEnumerable<SystemEventRecord> records, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemEvent>>([]);

        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> PurgeAsync(
            DateTime defaultCutoff,
            IReadOnlyDictionary<string, DateTime?> topicCutoffs,
            CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> GetEventTypesAsync(string? category = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
