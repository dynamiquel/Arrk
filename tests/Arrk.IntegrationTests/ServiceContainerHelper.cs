using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Arrk.Docker;
using Arrk.TestSupport;

namespace Arrk.IntegrationTests;

/// <summary>
/// Helper for spinning up real service containers for integration testing.
/// </summary>
public static class ServiceContainerHelper
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    public static DockerClient CreateClient() => DockerClientFactory.Create();

    public static async Task<string> EnsureImage(string image)
    {
        using DockerClient client = CreateClient();
        string[] imageParts = image.Split(':');
        string imageName = imageParts[0];
        string tag = imageParts.Length > 1 ? imageParts[1] : "latest";

        try
        {
            await client.Images.InspectImageAsync($"{imageName}:{tag}");
        }
        catch
        {
            ImagesCreateParameters createParams = new ImagesCreateParameters { FromImage = imageName, Tag = tag };
            await client.Images.CreateImageAsync(createParams, new(), new Progress<JSONMessage>());
        }
        return image;
    }

    public static async Task<string> CreateNetwork(string name)
    {
        using DockerClient client = CreateClient();
        NetworkManager nm = new(client);
        return await nm.CreateOrGetNetwork(name, new Dictionary<string, string>
        {
            ["arrk.test"] = "true",
            [TestRun.RunLabel] = TestRun.Id
        });
    }

    public static async Task<(string ContainerId, int MappedPort)> StartServiceContainer(
        string image, string name, string networkId,
        int containerPort, Dictionary<string, string>? env = null)
    {
        using DockerClient client = CreateClient();
        int hostPort = GetAvailablePort();

        CreateContainerParameters parameters = new CreateContainerParameters
        {
            Image = image,
            Name = name,
            Env = env?.Select(e => $"{e.Key}={e.Value}").ToList() ?? [],
            Labels = new Dictionary<string, string>
            {
                ["arrk.test"] = "true",
                [TestRun.RunLabel] = TestRun.Id
            },
            HostConfig = new()
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [$"{containerPort}/tcp"] = [new() { HostPort = hostPort.ToString() }]
                }
            },
            NetworkingConfig = new()
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkId] = new()
                }
            }
        };

        CreateContainerResponse? response = await client.Containers.CreateContainerAsync(parameters);
        await client.Containers.StartContainerAsync(response.ID, new());
        return (response.ID, hostPort);
    }

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

    public static async Task<bool> WaitForHttpReady(int port, TimeSpan timeout, string path = "/")
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                HttpResponseMessage response = await http.GetAsync($"http://localhost:{port}{path}");
                if ((int)response.StatusCode < 500)
                    return true;
            }
            catch { }
            await Task.Delay(2000);
        }
        return false;
    }

    public static async Task RemoveContainer(string containerId)
    {
        try
        {
            using DockerClient client = CreateClient();
            await client.Containers.StopContainerAsync(containerId, new() { WaitBeforeKillSeconds = 5 });
            await client.Containers.RemoveContainerAsync(containerId, new() { Force = true, RemoveVolumes = true });
        }
        catch { }
    }

    /// <summary>
    /// Force-removes any container with the given name so a fixed-name test fixture can be
    /// recreated even after an interrupted previous run.
    /// </summary>
    public static async Task RemoveContainerByName(string name)
    {
        try
        {
            using DockerClient client = CreateClient();
            ContainerListResponse? existing = (await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true })).FirstOrDefault(c => c.Names.Contains($"/{name}"));
            if (existing != null)
                await client.Containers.RemoveContainerAsync(existing.ID,
                    new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }
        catch { }
    }

    public static async Task<string> ExecInContainer(string containerId, string command)
    {
        using DockerClient client = CreateClient();
        ContainerExecCreateParameters execParams = new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            Cmd = new[] { "/bin/sh", "-c", command }
        };

        ContainerExecCreateResponse? execResponse = await client.Exec.ExecCreateContainerAsync(containerId, execParams);

        using MultiplexedStream? stream = await client.Exec.StartAndAttachContainerExecAsync(execResponse.ID, false);
        (string? stdout, string? stderr) = await stream.ReadOutputToEndAsync(CancellationToken.None);

        return stdout;
    }

    /// <summary>
    /// Creates a directory inside a LinuxServer.io-style container owned by the app user
    /// (<c>abc</c>) so the arr API accepts it as a root folder.
    /// </summary>
    public static async Task<string> CreateWritableMediaDir(string containerId, string path)
    {
        await ExecInContainer(containerId, $"mkdir -p {path} && chown abc:abc {path} && chmod 777 {path}");
        return path;
    }

    public static async Task<string> GetRadarrApiKey(string containerId)
    {
        string xml = await ExecInContainer(containerId, "cat /config/config.xml");
        Match match = System.Text.RegularExpressions.Regex.Match(xml, @"<ApiKey>([^<]+)</ApiKey>");
        return match.Success ? match.Groups[1].Value : "";
    }

    public static async Task<string> GetSonarrApiKey(string containerId)
    {
        string xml = await ExecInContainer(containerId, "cat /config/config.xml");
        Match match = System.Text.RegularExpressions.Regex.Match(xml, @"<ApiKey>([^<]+)</ApiKey>");
        return match.Success ? match.Groups[1].Value : "";
    }

    public static async Task DeleteNetwork(string networkId)
    {
        try
        {
            using DockerClient client = CreateClient();
            await client.Networks.DeleteNetworkAsync(networkId);
        }
        catch { }
    }

    static int GetAvailablePort()
    {
        TcpListener listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
