using Microsoft.Extensions.Configuration;
using Arrk.Core.Models;

namespace Arrk.Core.Services;

/// <summary>
/// Read-only definition of what a service IS, its identity, containers, volumes, config.
/// Modules create these dynamically so they can adapt to config, runtime, etc.
/// </summary>
public interface IServiceDefinition
{
    string Key { get; }
    string DisplayName { get; }
    string Description { get; }
    bool IsInfrastructure { get; }

    bool IsEnabled(IConfiguration configuration);
    IReadOnlyList<ContainerSpec> BuildContainerSpecs(IConfiguration configuration, VolumePathResolver volumeResolver, ArrkInstance arrk);

    IReadOnlyList<string> ProvisionBefore { get; }
    IReadOnlyList<string> ProvisionAfter { get; }
}
