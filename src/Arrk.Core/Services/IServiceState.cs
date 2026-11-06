using Arrk.Core.Enums;
using Arrk.Core.Models;

namespace Arrk.Core.Services;

/// <summary>
/// Mutable runtime state of a service, containers, health, metrics.
/// Updated by ServiceStateManager as Docker state changes.
/// </summary>
public interface IServiceState
{
    string ServiceKey { get; set; }
    bool IsEnabled { get; set; }
    List<ContainerInfo> Containers { get; set; }
    ContainerState AggregateState { get; set; }
    DateTime LastChecked { get; set; }
    ServiceMetrics? Metrics { get; set; }
    bool IsHealthy { get; }
}
