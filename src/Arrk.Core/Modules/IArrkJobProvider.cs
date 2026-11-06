using System.Linq.Expressions;

namespace Arrk.Core.Modules;

public interface IArrkJobProvider
{
    IEnumerable<IArrkJobRecord> Jobs { get; }

    IArrkJobProvider RegisterScheduled<T>(
        string jobName,
        Expression<Func<T, Task>> methodCall,
        string cronExpression,
        string? queue = null);

    IArrkJobProvider RegisterOptionsChange<TOptions, TJob>(
        Expression<Func<TJob, Task>> methodCall,
        string? queue = null);

    /// <summary>
    /// Re-registers an already-scheduled job with a new cron expression (used when a schedule is
    /// configurable, e.g. the speed test schedule). A no-op for an unknown job name.
    /// </summary>
    void UpdateSchedule(string jobName, string cronExpression);
}

public static class ArrkJobProviderExtensions
{
    extension(IArrkJobProvider provider)
    {
        public IArrkJobProvider RegisterScheduled<T>(
            Expression<Func<T, Task>> methodCall,
            string cronExpression,
            string? queue = null)
        {
            string jobClassName = typeof(T).Name;
            string jobName = jobClassName.Replace("job", string.Empty, StringComparison.OrdinalIgnoreCase);
            return provider.RegisterScheduled(jobName, methodCall, cronExpression, queue);
        }

        public IArrkJobProvider RegisterScheduledAndOptionsChange<TJob, TOptions>(
            Expression<Func<TJob, Task>> methodCall,
            string cronExpression,
            string? queue = null)
        {
            provider.RegisterScheduled(methodCall, cronExpression, queue);
            provider.RegisterOptionsChange<TOptions, TJob>(methodCall, queue);
            return provider;
        }

        /// <summary>Updates the schedule of a previously registered job (see <see cref="RegisterScheduled{T}"/>).</summary>
        public void UpdateSchedule<TJob>(string cronExpression)
        {
            string jobClassName = typeof(TJob).Name;
            string jobName = jobClassName.Replace("job", string.Empty, StringComparison.OrdinalIgnoreCase);
            provider.UpdateSchedule(jobName, cronExpression);
        }
    }
}