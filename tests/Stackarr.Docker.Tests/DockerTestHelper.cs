using Docker.DotNet;
using Docker.DotNet.Models;

namespace Stackarr.Docker.Tests;

public static class DockerTestHelper
{
    /// <summary>Small, fast image used for container lifecycle/query tests.</summary>
    public const string TestImage = "nginx:alpine";

    public static DockerClient CreateClient() => DockerClientFactory.Create();

    public static async Task<bool> IsDockerAvailable()
    {
        try
        {
            using DockerClient client = CreateClient();
            await client.System.GetSystemInfoAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> CreateContainer(
        string image,
        string name,
        Dictionary<string, string>? labels = null,
        string? networkId = null,
        int? hostPort = null,
        string containerPort = "80/tcp")
    {
        using DockerClient client = CreateClient();

        Dictionary<string, string> effectiveLabels = labels is null
            ? []
            : new Dictionary<string, string>(labels);
        effectiveLabels["stackarr.test"] = "true";
        effectiveLabels[Stackarr.TestSupport.TestRun.RunLabel] = Stackarr.TestSupport.TestRun.Id;

        CreateContainerParameters parameters = new()
        {
            Image = image,
            Name = name,
            Labels = effectiveLabels
        };

        if (networkId != null)
        {
            parameters.NetworkingConfig = new()
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkId] = new()
                }
            };
        }

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
        await client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
        return response.ID;
    }
}
