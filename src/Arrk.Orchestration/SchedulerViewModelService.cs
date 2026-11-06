using System.Reflection;
using Hangfire;
using Hangfire.Storage;

namespace Arrk.Orchestration;

public class SchedulerViewModelService(IRecurringJobManager recurringJobManager)
{
    public List<JobViewModel> GetJobs()
    {
        using IStorageConnection connection = JobStorage.Current.GetConnection();
        List<RecurringJobDto>? recurringJobs = connection.GetRecurringJobs();

        return recurringJobs?.Select(dto =>
        {
            bool isRemoved = dto.Removed;
            string state = isRemoved ? "Removed" : dto.LastJobState ?? "Pending";
            string jobName = dto.Job.Method.GetCustomAttribute<JobDisplayNameAttribute>()?.DisplayName ?? dto.Job.Method.Name;

            return new JobViewModel
            {
                JobId = dto.Id,
                DisplayName = jobName,
                CronExpression = dto.Cron,
                IsEnabled = !dto.Removed,
                IsRunning = state == "Processing",
                IsError = state == "Failed",
                LastRunTime = dto.LastExecution,
                NextExecution = dto.NextExecution,
                LastError = state == "Failed" ? dto.Error : null,
                LastJobState = state
            };
        }).ToList() ?? [];
    }

    public void TriggerJob(string jobId)
    {
        RecurringJob.Trigger(jobId);
    }

    public void ToggleJob(string jobId)
    {
        using IStorageConnection connection = JobStorage.Current.GetConnection();
        JobData jobData = connection.GetJobData(jobId);

        if (jobData.State != "Removed")
        {
            recurringJobManager.RemoveIfExists(jobId);
        }
        else
        {
            recurringJobManager.AddOrUpdate(
                jobId,
                () => ThrowNotFound(jobId),
                "*/5 * * * * *",
                new() { TimeZone = TimeZoneInfo.Local });
        }
    }

    private static void ThrowNotFound(string jobId)
    {
        throw new InvalidOperationException($"Job '{jobId}' must be invoked via DI, not this method.");
    }
}

public class JobViewModel
{
    public string JobId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CronExpression { get; set; } = "";
    public bool IsEnabled { get; set; }
    public bool IsRunning { get; set; }
    public bool IsError { get; set; }
    public DateTime? LastRunTime { get; set; }
    public DateTime? NextExecution { get; set; }
    public string? LastError { get; set; }
    public string LastJobState { get; set; } = "";

    public string StatusLabel => IsRunning ? "Running" : IsError ? "Failed" : IsEnabled ? "Idle" : "Disabled";
    public string StatusVariant => IsRunning ? "success" : IsError ? "error" : IsEnabled ? "info" : "neutral";
    public string LastRunDisplay => LastRunTime.HasValue
        ? (DateTime.UtcNow - LastRunTime.Value).TotalMinutes < 1
            ? "Just now"
            : (DateTime.UtcNow - LastRunTime.Value).TotalHours < 1
                ? $"{(int)(DateTime.UtcNow - LastRunTime.Value).TotalMinutes}m ago"
                : LastRunTime.Value.ToLocalTime().ToString("HH:mm:ss")
        : "Never";
    public string NextRunDisplay => NextExecution.HasValue
        ? NextExecution.Value.ToLocalTime().ToString("HH:mm:ss")
        : "Not scheduled";
}
