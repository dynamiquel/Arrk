using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Stackarr.TestSupport;

/// <summary>
/// Minimal Docker container helper shared by the module test projects that need to spin up a real
/// application container (Sonarr/Radarr/Prowlarr/Transmission/nginx) for integration tests.
/// Tests skip themselves when Docker is unavailable.
/// </summary>
public static class DockerTestContainer
{
    public static DockerClient CreateClient()
    {
        string socketPath = Environment.GetEnvironmentVariable("DOCKER_SOCKET") ?? "/var/run/docker.sock";
        return new DockerClientConfiguration(new Uri($"unix://{socketPath}")).CreateClient();
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

    public static async Task EnsureImage(string image)
    {
        using DockerClient client = CreateClient();
        string[] parts = image.Split(':');
        string name = parts[0];
        string tag = parts.Length > 1 ? parts[1] : "latest";
        try
        {
            await client.Images.InspectImageAsync($"{name}:{tag}");
        }
        catch
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = name, Tag = tag },
                new(),
                new Progress<JSONMessage>());
        }
    }

    public static async Task<string> CreateNetwork(string name)
    {
        using DockerClient client = CreateClient();
        var response = await client.Networks.CreateNetworkAsync(
            new NetworksCreateParameters
            {
                Name = name,
                Driver = "bridge",
                Labels = new Dictionary<string, string> { [TestRun.RunLabel] = TestRun.Id }
            });
        return response.ID;
    }

    public static async Task RemoveContainerByName(string name)
    {
        try
        {
            using DockerClient client = CreateClient();
            ContainerListResponse? existing = (await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true })).FirstOrDefault(c => c.Names.Contains($"/{name}"));
            if (existing != null)
                await DockerTestCleanup.RemoveOwnedContainerAsync(client, existing.ID);
        }
        catch { }
    }

    public static async Task<(string ContainerId, int HostPort)> Start(
        string image,
        string name,
        string? networkId,
        int containerPort,
        Dictionary<string, string>? env = null,
        Dictionary<string, string>? labels = null,
        string? networkAlias = null,
        List<string>? binds = null)
    {
        using DockerClient client = CreateClient();
        int hostPort = GetAvailablePort();

        Dictionary<string, string> effectiveLabels = labels is null
            ? []
            : new Dictionary<string, string>(labels);
        effectiveLabels["stackarr.test"] = "true";
        effectiveLabels[TestRun.RunLabel] = TestRun.Id;

        CreateContainerParameters parameters = new()
        {
            Image = image,
            Name = name,
            Env = env?.Select(e => $"{e.Key}={e.Value}").ToList() ?? [],
            Labels = effectiveLabels,
            HostConfig = new()
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [$"{containerPort}/tcp"] = [new() { HostPort = hostPort.ToString() }]
                },
                Binds = binds ?? []
            }
        };

        if (!string.IsNullOrEmpty(networkId))
        {
            parameters.NetworkingConfig = new()
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkId] = networkAlias is null
                        ? new EndpointSettings()
                        : new EndpointSettings { Aliases = [networkAlias] }
                }
            };
        }

        CreateContainerResponse response = await client.Containers.CreateContainerAsync(parameters);
        await client.Containers.StartContainerAsync(response.ID, new());
        return (response.ID, hostPort);
    }

    public static async Task<bool> WaitForHttp(int port, string path, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
                HttpResponseMessage response = await http.GetAsync($"http://localhost:{port}{path}");
                if ((int)response.StatusCode < 500)
                    return true;
            }
            catch { }
            await Task.Delay(2000);
        }
        return false;
    }

    /// <summary>
    /// Returns the combined stdout/stderr of a container. Used by tests to capture what a real
    /// HTTP echo container received (including request bodies). Demultiplexes Docker's log frame
    /// format when present (non-TTY containers).
    /// </summary>
    public static async Task<string> GetLogs(string containerId)
    {
        using DockerClient client = CreateClient();
        using Stream stream = await client.Containers.GetContainerLogsAsync(
            containerId,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = "1000" });

        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer);
        return Demultiplex(buffer.ToArray());
    }

    static string Demultiplex(byte[] data)
    {
        StringBuilder output = new();
        int i = 0;
        while (i + 8 <= data.Length)
        {
            // Frame header: [stream type][0][0][0][big-endian length].
            if (data[i + 1] != 0 || data[i + 2] != 0 || data[i + 3] != 0)
            {
                output.Append(System.Text.Encoding.UTF8.GetString(data, i, data.Length - i));
                return output.ToString();
            }

            int size = (data[i + 4] << 24) | (data[i + 5] << 16) | (data[i + 6] << 8) | data[i + 7];
            i += 8;

            if (size < 0 || i + size > data.Length)
            {
                output.Append(System.Text.Encoding.UTF8.GetString(data, i, data.Length - i));
                return output.ToString();
            }

            output.Append(System.Text.Encoding.UTF8.GetString(data, i, size));
            i += size;
        }

        if (i < data.Length)
            output.Append(System.Text.Encoding.UTF8.GetString(data, i, data.Length - i));

        return output.ToString();
    }

    public static async Task<string> Exec(string containerId, string command)
    {
        using DockerClient client = CreateClient();
        ContainerExecCreateParameters execParams = new()
        {
            AttachStdout = true,
            AttachStderr = true,
            Cmd = ["/bin/sh", "-c", command]
        };
        ContainerExecCreateResponse execResponse = await client.Exec.ExecCreateContainerAsync(containerId, execParams);
        using MultiplexedStream stream = await client.Exec.StartAndAttachContainerExecAsync(execResponse.ID, false);
        (string? stdout, _) = await stream.ReadOutputToEndAsync(CancellationToken.None);
        return stdout;
    }

    public static async Task<string> ReadApiKey(string containerId)
    {
        string xml = await Exec(containerId, "cat /config/config.xml");
        Match match = Regex.Match(xml, @"<ApiKey>([^<]+)</ApiKey>");
        return match.Success ? match.Groups[1].Value : "";
    }

    public static async Task RemoveContainer(string containerId)
    {
        if (string.IsNullOrEmpty(containerId))
            return;
        try
        {
            using DockerClient client = CreateClient();
            await DockerTestCleanup.RemoveOwnedContainerAsync(client, containerId);
        }
        catch { }
    }

    public static async Task DeleteNetwork(string networkId)
    {
        if (string.IsNullOrEmpty(networkId))
            return;
        try
        {
            using DockerClient client = CreateClient();
            await client.Networks.DeleteNetworkAsync(networkId);
        }
        catch { }
    }

    public static int GetAvailablePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
