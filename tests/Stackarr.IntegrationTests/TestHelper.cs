using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Stackarr.App;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Docker;
using Stackarr.TestSupport;

namespace Stackarr.IntegrationTests;

public static class TestHelper
{
    public static DockerClient CreateClient() => DockerClientFactory.Create();

    /// <summary>
    /// Builds the real registry (via the module loader) so integration tests exercise the
    /// same service definitions and ordering the application uses at startup.
    /// </summary>
    public static ServiceRegistry CreateDefault() => ServiceRegistryFactory.CreateModuleLoader().BuildRegistry();

    public static StackarrInstance CreateStackarrInstance(string? stackName = null)
    {
        string baseName = stackName ?? Guid.NewGuid().ToString("N")[..8];

        // Idempotent: helpers are often handed an already run-prefixed StackName.
        string name;
        if (baseName.StartsWith(TestRun.InstancePrefix, StringComparison.Ordinal))
        {
            name = baseName;
        }
        else
        {
            if (baseName.StartsWith("test-", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[5..];
            name = $"{TestRun.InstancePrefix}-{baseName}";
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = name })
            .Build();
        // Share one network across the whole suite to avoid exhausting Docker's default pool.
        return new StackarrInstance(configuration, networkName: TestRun.SharedNetworkName);
    }

    /// <summary>
    /// Polls until the named container reaches the expected state (or times out).
    /// </summary>
    public static async Task<ContainerInfo?> WaitForContainerState(
        ContainerManager containerManager,
        string name,
        Core.Enums.ContainerState expected,
        TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(20);
        DateTime deadline = DateTime.UtcNow + limit;
        ContainerInfo? container = null;

        while (DateTime.UtcNow < deadline)
        {
            container = await containerManager.GetContainer(name);
            if (container != null && container.State == expected)
                return container;
            await Task.Delay(250);
        }

        return container;
    }

    public static async Task<string> CreateContainerOnNetwork(
        DockerClient client,
        string image,
        string name,
        string networkId,
        Dictionary<string, string>? labels = null,
        int? hostPort = null,
        string containerPort = "80/tcp")
    {
        CreateContainerParameters parameters = new()
        {
            Image = image,
            Name = name,
            Labels = MergeRunLabels(labels),
            NetworkingConfig = new()
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkId] = new()
                }
            }
        };

        if (hostPort.HasValue)
        {
            parameters.HostConfig = new()
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [containerPort] = [new() { HostPort = hostPort.Value.ToString() }]
                }
            };
        }

        CreateContainerResponse? response = await client.Containers.CreateContainerAsync(parameters);
        ContainerStartParameters startParams = new();
        await client.Containers.StartContainerAsync(response.ID, startParams);
        return response.ID;
    }

    static Dictionary<string, string> MergeRunLabels(Dictionary<string, string>? labels)
    {
        Dictionary<string, string> merged = labels is null
            ? new Dictionary<string, string> { [ContainerLabels.Managed] = ContainerLabels.ManagedValue }
            : new Dictionary<string, string>(labels);
        merged["stackarr.test"] = "true";
        merged[TestRun.RunLabel] = TestRun.Id;
        return merged;
    }
}
