using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Arrk.Ai;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Docker;

namespace Arrk.Orchestration.Tools;

/// <summary>
/// Host, storage, job and image state for administrators. The pack resolves the concrete
/// orchestration services from the per-turn scope, so the module can be loaded without those
/// services being registered in every test container. All tools are admin-only: the same data is
/// already admin-only in the Docker, Scheduler and Infrastructure pages.
/// </summary>
public sealed class OrchestrationAssistantTool : IAssistantTool
{
    public string Key => "ops";
    public string Name => "Host & operations";
    public string Description => "Read host CPU/memory, service health, storage, jobs, image updates and recent service events.";
    public AssistantAccess Access => AssistantAccess.Admin;

    public IReadOnlyList<string> Keywords =>
    [
        "host", "server", "cpu", "memory", "ram", "storage", "disk", "space", "volume", "job",
        "scheduler", "container", "docker", "service", "health", "unhealthy", "down", "image update",
        "update available", "resource", "system status", "ops"
    ];

    public bool IsAvailable => true;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                async (CancellationToken ct = default) => await HostAsync(context, ct),
                "ops_host_status",
                "Reads host CPU/memory and the running/unhealthy Arrk services, with each service's state and container count."),

            AIFunctionFactory.Create(
                async (CancellationToken ct = default) => await StorageAsync(context, ct),
                "ops_storage",
                "Reads media/downloads filesystem usage (total, used, free) and Docker volume sizes."),

            AIFunctionFactory.Create(
                () => Jobs(context),
                "ops_jobs",
                "Lists recurring jobs with their schedule, last run, next run and last error."),

            AIFunctionFactory.Create(
                async (int limit = 10, CancellationToken ct = default) => await ImageUpdatesAsync(context, limit, ct),
                "ops_image_updates",
                "Lists services with a newer container image available. This can take a few seconds while registries are checked."),

            AIFunctionFactory.Create(
                async (int limit = 15, CancellationToken ct = default) => await EventsAsync(context, limit, ct),
                "ops_recent_events",
                "Lists recent health, service and storage events (down/recovered, storage warnings) from the durable event log.")
        ];
    }

    static async Task<JsonElement> HostAsync(AssistantToolContext context, CancellationToken ct)
    {
        ServiceStateManager? states = context.Services.GetService<ServiceStateManager>();
        if (states is null)
            return Json(new { error = "Service state is not available in this instance." });

        List<Service> services;
        try
        {
            services = await states.GetAllServices(ct);
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Could not read service state: {ex.Message}" });
        }

        List<Service> enabled = services.Where(s => s.State.IsEnabled).ToList();
        List<object> unhealthy = enabled
            .Where(s => !s.State.IsHealthy)
            .Select(s => (object)new
            {
                key = s.Definition.Key,
                name = s.Definition.DisplayName,
                state = s.State.AggregateState.ToString(),
                containers = s.State.Containers.Count,
                lastChecked = s.State.LastChecked
            })
            .ToList();

        HostMetrics host = context.Services.GetService<HostMetricsService>()?.GetHostMetrics() ?? new HostMetrics();

        return Json(new
        {
            host = new
            {
                cpuPercent = Math.Round(host.CpuPercent, 1),
                memoryUsedBytes = host.MemoryUsedBytes,
                memoryTotalBytes = host.MemoryTotalBytes,
                memoryPercent = Math.Round(host.MemoryPercent, 1)
            },
            services = new
            {
                total = services.Count,
                enabled = enabled.Count,
                running = enabled.Count(s => s.State.IsHealthy),
                notRunning = unhealthy.Count
            },
            unhealthy
        });
    }

    static async Task<JsonElement> StorageAsync(AssistantToolContext context, CancellationToken ct)
    {
        IOptionsMonitor<ArrkConfig>? config = context.Services.GetService<IOptionsMonitor<ArrkConfig>>();
        if (config is null)
            return Json(new { error = "Storage configuration is not available." });

        VolumePathResolver resolver = new(config.CurrentValue.Storage);
        List<object> roots = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach ((string key, string label) in new[]
                 {
                     (RelativeVolumes.Media, "Media"),
                     (RelativeVolumes.Downloads, "Downloads")
                 })
        {
            string path;
            try
            {
                path = resolver.ResolveToLocalPath(key);
            }
            catch
            {
                continue;
            }

            if (!DockerMetricsCollector.GetDiskSpace(path, out long total, out long available, out long used) || total <= 0)
                continue;

            string filesystem = DockerMetricsCollector.GetFileSystemId(path, out ulong id)
                ? id.ToString()
                : path;
            if (!seen.Add(filesystem))
                continue;

            roots.Add(new
            {
                label,
                path,
                totalBytes = total,
                usedBytes = used,
                availableBytes = available,
                usedPercent = total > 0 ? Math.Round((double)used / total * 100, 1) : 0
            });
        }

        List<object> volumes = [];
        DockerMetricsCollector? metrics = context.Services.GetService<DockerMetricsCollector>();
        if (metrics is not null)
        {
            try
            {
                List<VolumeMetrics> found = await metrics.GetVolumeMetrics(ct);
                volumes = found
                    .OrderByDescending(v => v.UsedBytes)
                    .Take(15)
                    .Select(v => (object)new
                    {
                        name = v.Name,
                        usedBytes = v.UsedBytes,
                        totalBytes = v.TotalBytes,
                        usedPercent = Math.Round(v.UsedPercent, 1),
                        containers = v.Containers
                    })
                    .ToList();
            }
            catch
            {
                // Volume probing is best-effort; the root filesystems still answer the question.
            }
        }

        return Json(new { roots, volumes });
    }

    static JsonElement Jobs(AssistantToolContext context)
    {
        SchedulerViewModelService? scheduler = context.Services.GetService<SchedulerViewModelService>();
        if (scheduler is null)
            return Json(new { error = "The scheduler is not available in this instance." });

        List<JobViewModel> jobs;
        try
        {
            jobs = scheduler.GetJobs();
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Could not read jobs: {ex.Message}" });
        }

        List<object> items = jobs
            .Select(j => (object)new
            {
                id = j.JobId,
                name = j.DisplayName,
                cron = j.CronExpression,
                enabled = j.IsEnabled,
                running = j.IsRunning,
                error = j.IsError,
                lastState = j.LastJobState,
                lastRun = j.LastRunTime,
                nextRun = j.NextExecution,
                lastError = j.LastError
            })
            .ToList();

        return Json(new
        {
            count = items.Count,
            failing = jobs.Count(j => j.IsError),
            items
        });
    }

    static async Task<JsonElement> ImageUpdatesAsync(
        AssistantToolContext context, int limit, CancellationToken ct)
    {
        IImageUpdateService? updates = context.Services.GetService<IImageUpdateService>();
        if (updates is null)
            return Json(new { error = "Image update checks are not available in this instance." });

        try
        {
            List<ServiceUpdateInfo> all = await updates.GetServiceUpdateInfosAsync(ct);
            List<object> items = all
                .Where(u => u.UpdateAvailable)
                .Take(Math.Clamp(limit <= 0 ? 10 : limit, 1, 25))
                .Select(u => (object)new
                {
                    service = u.ServiceKey,
                    name = u.DisplayName,
                    image = u.Image
                })
                .ToList();

            return Json(new
            {
                count = items.Count,
                note = "Digest-pinned images only; a tag-only image can be current without appearing here.",
                items
            });
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Could not check image updates: {ex.Message}" });
        }
    }

    static async Task<JsonElement> EventsAsync(
        AssistantToolContext context, int limit, CancellationToken ct)
    {
        IEventStore? events = context.Services.GetService<IEventStore>();
        if (events is null)
            return Json(new { error = "The event log is not available." });

        int take = Math.Clamp(limit <= 0 ? 15 : limit, 1, 50);
        DateTime since = DateTime.UtcNow.AddDays(-7);
        List<SystemEvent> rows = [];

        foreach (string category in new[] { EventCategory.Health, EventCategory.Service, EventCategory.Storage })
        {
            try
            {
                rows.AddRange(await events.QueryAsync(new SystemEventQuery
                {
                    Category = category,
                    Since = since,
                    Limit = take
                }, ct));
            }
            catch
            {
                // A missing category must not hide the others.
            }
        }

        List<object> items = rows
            .OrderByDescending(e => e.Timestamp)
            .Take(take)
            .Select(e => (object)new
            {
                timestamp = e.Timestamp,
                category = e.Category.ToString(),
                type = e.EventType,
                severity = e.Severity.ToString(),
                message = e.Message,
                name = e.SourceName
            })
            .ToList();

        return Json(new { count = items.Count, since, items });
    }

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
