using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using Arrk.Docker;
using Arrk.TestSupport;

namespace Arrk.IntegrationTests;

public static class DockerHelper
{
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

    public static async Task<string> PullImage(string image)
    {
        using DockerClient client = CreateClient();
        string[] imageParts = image.Split(':');
        string imageName = imageParts[0];
        string tag = imageParts.Length > 1 ? imageParts[1] : "latest";

        ImagesCreateParameters createParams = new()
        {
            FromImage = imageName,
            Tag = tag
        };
        await client.Images.CreateImageAsync(createParams, new(), new Progress<JSONMessage>());

        return image;
    }

    public static async Task<string> CreateNetwork(string networkName)
    {
        using DockerClient client = CreateClient();
        NetworkManager networkManager = new(client);
        return await networkManager.CreateOrGetNetwork(networkName, RunLabels());
    }

    /// <summary>Labels stamped on every resource this test run creates.</summary>
    public static Dictionary<string, string> RunLabels() => new()
    {
        ["arrk.test"] = "true",
        [TestRun.RunLabel] = TestRun.Id
    };

    public static async Task<string> StartContainer(
        string image, string name, string networkId,
        Dictionary<string, string>? labels = null,
        int? hostPort = null, string containerPort = "80/tcp")
    {
        using DockerClient client = CreateClient();

        Dictionary<string, string> effectiveLabels = labels is null
            ? []
            : new Dictionary<string, string>(labels);
        effectiveLabels["arrk.test"] = "true";
        effectiveLabels[TestRun.RunLabel] = TestRun.Id;

        CreateContainerParameters parameters = new()
        {
            Image = image,
            Name = name,
            Labels = effectiveLabels,
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

    public static async Task StopContainer(string containerId)
    {
        using DockerClient client = CreateClient();
        ContainerStopParameters stopParams = new()
        {
            WaitBeforeKillSeconds = 5
        };
        await client.Containers.StopContainerAsync(containerId, stopParams);
    }

    public static async Task RemoveContainer(string containerId, bool force = true)
    {
        using DockerClient client = CreateClient();
        try
        {
            ContainerRemoveParameters removeParams = new()
            {
                Force = force,
                RemoveVolumes = true
            };
            await client.Containers.RemoveContainerAsync(containerId, removeParams);
        }
        catch { }
    }

    public static async Task DeleteNetwork(string networkId)
    {
        using DockerClient client = CreateClient();
        try
        {
            await client.Networks.DeleteNetworkAsync(networkId);
        }
        catch { }
    }

    public static async Task<bool> WaitForContainerReady(string containerId, Func<HttpClient, Task<bool>> checkFunc, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
                if (await checkFunc(client))
                    return true;
            }
            catch
            {
                // Not ready yet
            }
            await Task.Delay(2000);
        }
        return false;
    }
}
