using Stackarr.Core.Enums;
using Stackarr.Core.Models;

namespace Stackarr.Core.Services;

public class ServiceState : IServiceState
{
    public string ServiceKey { get; set; } = "";
    public bool IsEnabled { get; set; }
    public List<ContainerInfo> Containers { get; set; } = [];
    public ContainerState AggregateState { get; set; }
    public bool IsHealthy => AggregateState == ContainerState.Running;
    public DateTime LastChecked { get; set; }
    public ServiceMetrics? Metrics { get; set; }
}
