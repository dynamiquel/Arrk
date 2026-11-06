using Microsoft.Extensions.Configuration;
using Arrk.Core.Models;

namespace Arrk.Core.Services;

public class TestServiceSpec : IServiceDefinition
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public bool IsInfrastructure { get; init; }
    public required Func<IConfiguration, bool> IsEnabled { get; init; }
    public required Func<IConfiguration, VolumePathResolver, ArrkInstance, IReadOnlyList<ContainerSpec>> BuildContainerSpecs { get; init; }
    public IReadOnlyList<string> ProvisionBefore { get; init; } = [];
    public IReadOnlyList<string> ProvisionAfter { get; init; } = [];

    bool IServiceDefinition.IsEnabled(IConfiguration configuration) => IsEnabled(configuration);
    IReadOnlyList<ContainerSpec> IServiceDefinition.BuildContainerSpecs(IConfiguration configuration, VolumePathResolver volumeResolver, ArrkInstance arrk) =>
        BuildContainerSpecs(configuration, volumeResolver, arrk);
}
