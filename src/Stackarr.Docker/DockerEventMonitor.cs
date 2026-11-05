using System.Collections.Concurrent;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Models;

namespace Stackarr.Docker;

public class DockerEventMonitor(
    DockerClient client,
    ContainerManager containerManager,
    StackarrInstance stackarr,
    ILogger<DockerEventMonitor> logger)
    : BackgroundService
{
    readonly string _instanceId = stackarr.StackName;

    readonly ConcurrentQueue<DockerEvent> _recentEvents = new();
    readonly ConcurrentDictionary<string, ImagePullState> _activePulls = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, LiveContainerEvent> _liveContainerEvents = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _managedImageNames = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _imageLock = new();
    const int MaxRecentEvents = 200;
    const int ManagedImageRefreshSeconds = 30;
    DateTime _lastImageRefresh = DateTime.MinValue;

    public IReadOnlyCollection<DockerEvent> RecentEvents => _recentEvents.ToArray();
    public IReadOnlyDictionary<string, ImagePullState> ActivePulls => _activePulls;
    public IReadOnlyDictionary<string, LiveContainerEvent> LiveContainerEvents => _liveContainerEvents;

    public event Action<DockerEvent>? EventReceived;
    public event Action<ImagePullState>? PullProgressUpdated;
    public event Action? LiveEventsChanged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Docker event monitor starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ContainerEventsParameters parameters = new ContainerEventsParameters();
                Progress<Message> progress = new Progress<Message>(OnEventReceived);
                await client.System.MonitorEventsAsync(parameters, progress, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Docker event stream disconnected, reconnecting in 5s...");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    void OnEventReceived(Message message)
    {
        try
        {
            RefreshManagedImagesIfNeeded();

            if (!IsManagedEvent(message))
                return;

            DockerEventCategory category = CategorizeEvent(message.Type, message.Action);
            IDictionary<string, string>? attributes = message.Actor?.Attributes;
            string actorName = "";
            string image = "";

            if (attributes != null)
            {
                if (attributes.TryGetValue("name", out string? name))
                    actorName = name;
                if (attributes.TryGetValue("image", out string? img))
                    image = img;
            }

            DockerEvent dockerEvent = new DockerEvent
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(message.Time).DateTime,
                Type = message.Type ?? "",
                Action = message.Action ?? "",
                ActorId = message.ID ?? "",
                ActorName = actorName,
                Image = image,
                Category = category,
                Attributes = attributes?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new()
            };

            _recentEvents.Enqueue(dockerEvent);
            while (_recentEvents.Count > MaxRecentEvents)
            {
                _recentEvents.TryDequeue(out _);
            }

            HandleImagePullEvent(message, category, actorName);
            HandleLiveContainerEvent(dockerEvent, actorName);

            EventReceived?.Invoke(dockerEvent);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error processing Docker event");
        }
    }

    bool IsManagedEvent(Message message)
    {
        string? type = message.Type?.ToLowerInvariant();
        IDictionary<string, string>? attributes = message.Actor?.Attributes;

        return type switch
        {
            "container" => IsManagedContainerEvent(attributes),
            "image" => IsManagedImageEvent(attributes),
            "network" => IsManagedNetworkEvent(attributes),
            _ => false
        };
    }

    bool IsManagedContainerEvent(IDictionary<string, string>? attributes)
    {
        if (attributes == null)
            return false;
        if (!attributes.TryGetValue("stackarr.managed", out string? managed) || managed != "true")
            return false;
        if (!attributes.TryGetValue("stackarr.instance-id", out string? id) || id != _instanceId)
            return false;
        return true;
    }

    bool IsManagedImageEvent(IDictionary<string, string>? attributes)
    {
        if (attributes == null)
            return false;
        if (!attributes.TryGetValue("name", out string? imageName))
            return false;

        lock (_imageLock)
        {
            return _managedImageNames.Contains(imageName);
        }
    }

    bool IsManagedNetworkEvent(IDictionary<string, string>? attributes)
    {
        if (attributes == null)
            return false;
        if (!attributes.TryGetValue("name", out string? networkName))
            return false;
        return networkName.StartsWith(stackarr.ContainerPrefix, StringComparison.Ordinal);
    }

    void RefreshManagedImagesIfNeeded()
    {
        if ((DateTime.UtcNow - _lastImageRefresh).TotalSeconds < ManagedImageRefreshSeconds)
            return;
        _lastImageRefresh = DateTime.UtcNow;

        try
        {
            List<ContainerInfo> containers = containerManager
                .ListContainers(labelFilter: ContainerLabels.Managed)
                .GetAwaiter().GetResult();

            HashSet<string> images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ContainerInfo c in containers)
            {
                if (!c.Labels.TryGetValue(ContainerLabels.InstanceId, out string? id) || id != _instanceId)
                    continue;
                if (!string.IsNullOrEmpty(c.Image))
                    images.Add(c.Image);
            }

            lock (_imageLock)
            {
                _managedImageNames.Clear();
                foreach (string img in images)
                    _managedImageNames.Add(img);
            }
        }
        catch { }
    }

    void HandleLiveContainerEvent(DockerEvent evt, string actorName)
    {
        string key = string.IsNullOrEmpty(actorName) ? evt.ActorId : actorName;

        LiveContainerEvent liveEvent = new LiveContainerEvent
        {
            Event = evt,
            ContainerName = actorName,
            StartedAt = evt.Timestamp
        };

        _liveContainerEvents[key] = liveEvent;
        LiveEventsChanged?.Invoke();

        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(t =>
        {
            _liveContainerEvents.TryRemove(key, out _);
            LiveEventsChanged?.Invoke();
        });
    }

    void HandleImagePullEvent(Message message, DockerEventCategory category, string actorName)
    {
        if (category != DockerEventCategory.ImagePull)
            return;
        if (string.IsNullOrEmpty(actorName))
            return;

        if (message.Status == "Pulling fs layer" || message.Status == "Downloading" ||
            message.Status == "Extracting" || message.Status == "Pull complete")
        {
            ImagePullState state = _activePulls.GetOrAdd(actorName, name => new()
            {
                ImageName = name,
                StartedAt = DateTime.UtcNow
            });

            PullProgressUpdated?.Invoke(state);
        }
        else if (message.Action == "pull")
        {
            if (_activePulls.TryRemove(actorName, out ImagePullState? state))
            {
                state.IsCompleted = true;
                state.CompletedAt = DateTime.UtcNow;
                PullProgressUpdated?.Invoke(state);

                _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(t =>
                {
                    _activePulls.TryRemove(actorName, out _);
                });
            }
        }
    }

    public void ReportPullProgress(string imageName, string layerId, string status, long completed, long total)
    {
        ImagePullState state = _activePulls.GetOrAdd(imageName, name => new()
        {
            ImageName = name,
            StartedAt = DateTime.UtcNow
        });

        if (!string.IsNullOrEmpty(layerId))
        {
            if (!state.Layers.TryGetValue(layerId, out ImagePullLayerProgress? layer))
            {
                layer = new() { LayerId = layerId };
                state.Layers[layerId] = layer;
            }

            layer.Status = status;
            layer.CompletedBytes = completed;
            layer.TotalBytes = total;
        }

        PullProgressUpdated?.Invoke(state);
    }

    public void ReportPullComplete(string imageName, bool success, string? error = null)
    {
        if (_activePulls.TryRemove(imageName, out ImagePullState? state))
        {
            state.IsCompleted = true;
            state.CompletedAt = DateTime.UtcNow;
            if (!success)
            {
                state.IsError = true;
                state.ErrorMessage = error ?? "Unknown error";
            }
            else
            {
                foreach (ImagePullLayerProgress layer in state.Layers.Values)
                {
                    layer.CompletedBytes = layer.TotalBytes;
                }
            }
            PullProgressUpdated?.Invoke(state);

            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(t =>
            {
                _activePulls.TryRemove(imageName, out _);
            });
        }
    }

    static DockerEventCategory CategorizeEvent(string? type, string? action)
    {
        return (type?.ToLowerInvariant(), action?.ToLowerInvariant()) switch
        {
            ("image", "pull") => DockerEventCategory.ImagePull,
            ("image", "push") => DockerEventCategory.ImagePush,
            ("image", "delete") => DockerEventCategory.ImageDelete,
            ("container", "start") => DockerEventCategory.ContainerStart,
            ("container", "stop") => DockerEventCategory.ContainerStop,
            ("container", "die") => DockerEventCategory.ContainerDie,
            ("container", "create") => DockerEventCategory.ContainerCreate,
            ("container", "destroy") => DockerEventCategory.ContainerDestroy,
            ("container", "pause") => DockerEventCategory.ContainerPause,
            ("container", "unpause") => DockerEventCategory.ContainerUnpause,
            ("container", "restart") => DockerEventCategory.ContainerRestart,
            ("network", "create") => DockerEventCategory.NetworkCreate,
            ("network", "connect") => DockerEventCategory.NetworkConnect,
            ("network", "disconnect") => DockerEventCategory.NetworkDisconnect,
            ("network", "destroy") => DockerEventCategory.NetworkDestroy,
            ("volume", "create") => DockerEventCategory.VolumeCreate,
            ("volume", "mount") => DockerEventCategory.VolumeMount,
            ("volume", "unmount") => DockerEventCategory.VolumeUnmount,
            ("volume", "destroy") => DockerEventCategory.VolumeDestroy,
            _ => DockerEventCategory.Other
        };
    }
}

public class LiveContainerEvent
{
    public DockerEvent Event { get; set; } = new();
    public string ContainerName { get; set; } = "";
    public DateTime StartedAt { get; set; }
}
