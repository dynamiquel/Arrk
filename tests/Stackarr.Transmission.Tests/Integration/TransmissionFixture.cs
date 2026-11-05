using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using Stackarr.Docker;
using Stackarr.TestSupport;

namespace Stackarr.Transmission.Tests;

/// <summary>
/// Spins up a real Transmission container (linuxserver/transmission) and exposes its
/// mapped RPC port for integration tests. LinuxServer images only expose RPC through the
/// container's bundled nginx proxy when RPC is bound to localhost, so we bind RPC to 0.0.0.0
/// via an env override and talk JSON-RPC directly.
/// </summary>
public sealed class TransmissionFixture : IAsyncDisposable
{
    const string Image = "lscr.io/linuxserver/transmission:latest";
    static readonly string ContainerName = $"stackarr-test-transmission-{TestRun.Id}";
    const int RpcPort = 9091;

    public string ContainerId { get; private set; } = "";
    public string NetworkId { get; private set; } = "";
    public int HostPort { get; private set; }
    public bool Ready { get; private set; }

    public string Url => $"http://localhost:{HostPort}/transmission/rpc";
    public string Username => "transmission";
    public string Password => "stackarr";

    public async Task<bool> StartAsync()
    {
        if (!await IsDockerAvailable())
            return false;

        string networkName = $"stackarr-test-tx-{Guid.NewGuid():N}";
        NetworkId = await CreateNetwork(networkName);

        await RemoveExistingContainer();
        await EnsureImage();

        HostPort = GetAvailablePort();

        CreateContainerParameters parameters = new()
        {
            Image = Image,
            Name = ContainerName,
            Env =
            [
                "PUID=1000",
                "PGID=1000",
                "TZ=Etc/UTC",
                "USER=transmission",
                "PASS=stackarr",
                "RPC_USERNAME=transmission",
                "RPC_PASSWORD=stackarr",
                "RPC_WHITELIST_ENABLED=false",
                "RPC_AUTHENTICATION_REQUIRED=true",
                // Bind RPC to all interfaces so the host can reach it directly.
                "TRANSMISSION_RPC_BIND_ADDRESS=0.0.0.0"
            ],
            Labels = new Dictionary<string, string>
            {
                ["stackarr.test"] = "true",
                [TestRun.RunLabel] = TestRun.Id
            },
            HostConfig = new()
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [$"{RpcPort}/tcp"] = [new() { HostPort = HostPort.ToString() }]
                }
            },
            NetworkingConfig = new()
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings> { [NetworkId] = new() }
            }
        };

        using DockerClient client = CreateDockerClient();
        CreateContainerResponse response = await client.Containers.CreateContainerAsync(parameters);
        ContainerId = response.ID;
        await client.Containers.StartContainerAsync(ContainerId, new());

        Ready = await WaitForRpcReady(TimeSpan.FromMinutes(3));
        return Ready;
    }

    async Task<bool> WaitForRpcReady(TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        using TransmissionClient probe = new(Url, Username, Password);
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (await probe.TestConnection())
                    return true;
            }
            catch { }
            await Task.Delay(2000);
        }
        return false;
    }

    public TransmissionClient CreateClient() => new(Url, Username, Password);

    public async ValueTask DisposeAsync()
    {
        await RemoveContainer(ContainerId);
        await RemoveContainerByName(ContainerName);
        await DeleteNetwork(NetworkId);
    }

    // Docker helpers (kept local so the Transmission test project is self-contained)

    static DockerClient CreateDockerClient() => DockerClientFactory.Create();

    static async Task<bool> IsDockerAvailable()
    {
        try
        {
            using DockerClient client = CreateDockerClient();
            await client.System.GetSystemInfoAsync();
            return true;
        }
        catch { return false; }
    }

    static async Task EnsureImage()
    {
        using DockerClient client = CreateDockerClient();
        try
        {
            await client.Images.InspectImageAsync(Image);
        }
        catch
        {
            string[] parts = Image.Split(':');
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = parts[0], Tag = parts[1] },
                new(),
                new Progress<JSONMessage>());
        }
    }

    static async Task<string> CreateNetwork(string name)
    {
        using DockerClient client = CreateDockerClient();
        NetworkManager nm = new(client);
        return await nm.CreateOrGetNetwork(name, new Dictionary<string, string>
        {
            ["stackarr.test"] = "true",
            [TestRun.RunLabel] = TestRun.Id
        });
    }

    static async Task RemoveExistingContainer()
    {
        try
        {
            using DockerClient client = CreateDockerClient();
            ContainerListResponse? existing = (await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }))
                .FirstOrDefault(c => c.Names.Contains($"/{ContainerName}"));
            if (existing != null)
                await client.Containers.RemoveContainerAsync(existing.ID,
                    new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }
        catch { }
    }

    static async Task RemoveContainer(string containerId)
    {
        if (string.IsNullOrEmpty(containerId))
            return;
        try
        {
            using DockerClient client = CreateDockerClient();
            await client.Containers.StopContainerAsync(containerId, new() { WaitBeforeKillSeconds = 5 });
            await client.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }
        catch { }
    }

    static async Task RemoveContainerByName(string name)
    {
        try
        {
            using DockerClient client = CreateDockerClient();
            ContainerListResponse? existing = (await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }))
                .FirstOrDefault(c => c.Names.Contains($"/{name}"));
            if (existing != null)
                await client.Containers.RemoveContainerAsync(existing.ID,
                    new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }
        catch { }
    }

    static async Task DeleteNetwork(string networkId)
    {
        try
        {
            using DockerClient client = CreateDockerClient();
            await client.Networks.DeleteNetworkAsync(networkId);
        }
        catch { }
    }

    static int GetAvailablePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
