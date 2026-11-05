using System.Linq.Expressions;
using Stackarr.Core.Modules;

namespace Stackarr.TestSupport;

/// <summary>
/// Records jobs registered by a module without touching Hangfire. Shared across test projects.
/// </summary>
public sealed class FakeJobProvider : IStackarrJobProvider
{
    public List<(string JobName, Type JobType, string Cron, string? Queue)> Scheduled { get; } = [];
    public List<Type> OptionsChangeJobs { get; } = [];
    public List<(string JobName, string Cron)> Reschedules { get; } = [];

    public IEnumerable<IStackarrJobRecord> Jobs => Scheduled
        .Select(j => (IStackarrJobRecord)new StackarrJobRecord { JobName = j.JobName, JobType = j.JobType })
        .ToList();

    public IStackarrJobProvider RegisterScheduled<T>(
        string jobName,
        Expression<Func<T, Task>> methodCall,
        string cronExpression,
        string? queue = null)
    {
        Scheduled.Add((jobName, typeof(T), cronExpression, queue));
        return this;
    }

    public IStackarrJobProvider RegisterOptionsChange<TOptions, TJob>(
        Expression<Func<TJob, Task>> methodCall,
        string? queue = null)
    {
        OptionsChangeJobs.Add(typeof(TJob));
        return this;
    }

    public void UpdateSchedule(string jobName, string cronExpression)
    {
        Reschedules.Add((jobName, cronExpression));
        int index = Scheduled.FindIndex(j => j.JobName == jobName);
        if (index >= 0)
        {
            (string JobName, Type JobType, string Cron, string? Queue) existing = Scheduled[index];
            Scheduled[index] = (existing.JobName, existing.JobType, cronExpression, existing.Queue);
        }
    }
}
