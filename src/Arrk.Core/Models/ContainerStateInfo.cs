using Arrk.Core.Enums;

namespace Arrk.Core.Models;

public class ContainerStateInfo
{
    public string ContainerId { get; init; } = "";
    public string ContainerName { get; init; } = "";
    public string Image { get; init; } = "";
    public ContainerState State { get; init; }
    public string Status { get; init; } = "";
    public string? ServiceLabel { get; init; }
}
