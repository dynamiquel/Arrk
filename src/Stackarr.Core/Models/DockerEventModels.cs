namespace Stackarr.Core.Models;

public enum DockerEventCategory
{
    ImagePull,
    ImagePush,
    ImageDelete,
    ContainerStart,
    ContainerStop,
    ContainerDie,
    ContainerCreate,
    ContainerDestroy,
    ContainerPause,
    ContainerUnpause,
    ContainerRestart,
    NetworkCreate,
    NetworkConnect,
    NetworkDisconnect,
    NetworkDestroy,
    VolumeCreate,
    VolumeMount,
    VolumeUnmount,
    VolumeDestroy,
    Other
}

public class DockerEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string Action { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string Image { get; set; } = "";
    public DockerEventCategory Category { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new();
}

public class ImagePullState
{
    public string ImageName { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsError { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, ImagePullLayerProgress> Layers { get; set; } = new();
    public double OverallPercent => TotalBytes > 0 ? (double)CompletedBytes / TotalBytes * 100 : 0;
    public long TotalBytes => Layers.Values.Sum(l => l.TotalBytes);
    public long CompletedBytes => Layers.Values.Sum(l => l.CompletedBytes);
}

public class ImagePullLayerProgress
{
    public string LayerId { get; set; } = "";
    public string Status { get; set; } = "";
    public long CompletedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double Percent => TotalBytes > 0 ? (double)CompletedBytes / TotalBytes * 100 : 0;
}
