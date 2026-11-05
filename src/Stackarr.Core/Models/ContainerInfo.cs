using Stackarr.Core.Enums;

namespace Stackarr.Core.Models;

public class ContainerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public ContainerState State { get; set; }
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<string> Ports { get; set; } = [];
    public List<string> Networks { get; set; } = [];
    public long SizeRw { get; set; }
    public List<VolumeMount> Mounts { get; set; } = [];
}
