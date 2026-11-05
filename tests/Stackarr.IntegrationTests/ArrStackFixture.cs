using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Stackarr.Docker;
using Stackarr.TestSupport;

namespace Stackarr.IntegrationTests;

/// <summary>
/// Spins up a real Sonarr + Radarr + Prowlarr (+ Transmission) stack on a shared Docker network
/// so cross-module behaviour can be exercised end-to-end against the real applications.
///
/// All clients returned by this fixture point at the containers' published host ports, so the
/// test process talks to the real arr APIs (and to each other via container DNS names).
/// </summary>
public sealed class ArrStackFixture : IAsyncDisposable
{
    public const string NetworkAliasPrefix = "stackarr-it";

    /// <summary>Run-unique alias prefix so concurrent test runs never share container DNS names.</summary>
    public string AliasPrefix => $"{NetworkAliasPrefix}-{TestRun.Id}";

    /// <summary>Stackarr stack name whose <c>ContainerPrefix</c> matches <see cref="AliasPrefix"/>.</summary>
    public string StackName => $"it-{TestRun.Id}";

    public string NetworkId { get; private set; } = "";
    public string NetworkName { get; private set; } = "";

    public string SonarrContainerId { get; private set; } = "";
    public string RadarrContainerId { get; private set; } = "";
    public string ProwlarrContainerId { get; private set; } = "";
    public string TransmissionContainerId { get; private set; } = "";

    public int SonarrPort { get; private set; }
    public int RadarrPort { get; private set; }
    public int ProwlarrPort { get; private set; }
    public int TransmissionPort { get; private set; }

    public string SonarrApiKey { get; private set; } = "";
    public string RadarrApiKey { get; private set; } = "";
    public string ProwlarrApiKey { get; private set; } = "";

    /// <summary>Container DNS name of Sonarr as seen from other containers on the network.</summary>
    public string SonarrHost => $"{AliasPrefix}-sonarr";
    public string RadarrHost => $"{AliasPrefix}-radarr";
    public string ProwlarrHost => $"{AliasPrefix}-prowlarr";
    public string TransmissionHost => $"{AliasPrefix}-transmission";

    public Stackarr.Core.StackarrInstance? Instance { get; private set; }

    public bool Ready { get; private set; }

    public string SonarrUrl => $"http://localhost:{SonarrPort}/sonarr";
    public string RadarrUrl => $"http://localhost:{RadarrPort}/radarr";
    public string ProwlarrUrl => $"http://localhost:{ProwlarrPort}/prowlarr";

    public bool WithTransmission { get; init; } = true;

    /// <summary>
    /// Starts the stack. Returns false (leaving <see cref="Ready"/> false) when Docker is unavailable.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (!await DockerHelper.IsDockerAvailable())
            return false;

        try
        {
            NetworkName = $"stackarr-it-{TestRun.Id}-{Guid.NewGuid():N}";
            NetworkId = await DockerHelper.CreateNetwork(NetworkName);

            await EnsureImages();
            await RemoveStaleContainers();

            SonarrPort = GetAvailablePort();
            RadarrPort = GetAvailablePort();
            ProwlarrPort = GetAvailablePort();
            TransmissionPort = GetAvailablePort();

            SonarrContainerId = await StartArr("lscr.io/linuxserver/sonarr:latest", SonarrHost, 8989, SonarrPort,
                new Dictionary<string, string>
                {
                    ["SONARR__AUTH__APIKEY"] = "stackarr",
                    ["SONARR__SERVER__URLBASE"] = "/sonarr"
                });
            RadarrContainerId = await StartArr("lscr.io/linuxserver/radarr:latest", RadarrHost, 7878, RadarrPort,
                new Dictionary<string, string>
                {
                    ["RADARR__AUTH__APIKEY"] = "stackarr",
                    ["RADARR__SERVER__URLBASE"] = "/radarr"
                });
            ProwlarrContainerId = await StartArr("lscr.io/linuxserver/prowlarr:latest", ProwlarrHost, 9696, ProwlarrPort,
                new Dictionary<string, string>
                {
                    ["PROWLARR__AUTH__APIKEY"] = "stackarr",
                    ["PROWLARR__SERVER__URLBASE"] = "/prowlarr"
                });

            if (WithTransmission)
            {
                TransmissionContainerId = await StartTransmission(TransmissionHost, 9091, TransmissionPort);
            }

            bool sonarrReady = await WaitForHttp(SonarrPort, "/sonarr/api/v3/system/status", TimeSpan.FromMinutes(4));
            bool radarrReady = await WaitForHttp(RadarrPort, "/radarr/api/v3/system/status", TimeSpan.FromMinutes(4));
            bool prowlarrReady = await WaitForHttp(ProwlarrPort, "/prowlarr/api/v1/system/status", TimeSpan.FromMinutes(4));

            if (!sonarrReady || !radarrReady || !prowlarrReady)
                return false;

            // The API keys are pinned via env (matching production TvModule/ProwlarrModule keys).
            // Newer LinuxServer images do not always surface the key in config.xml, so fall back
            // to the known value when the file does not contain one.
            SonarrApiKey = await ApiKeyOr(SonarrContainerId, "stackarr");
            RadarrApiKey = await ApiKeyOr(RadarrContainerId, "stackarr");
            ProwlarrApiKey = await ApiKeyOr(ProwlarrContainerId, "stackarr");

            // The arrs seed their default quality profiles shortly after first start; wait for them
            // so profile-dependent tests are not racy.
            await WaitForQualityProfiles(SonarrUrl, SonarrApiKey, TimeSpan.FromMinutes(3));
            await WaitForQualityProfiles(RadarrUrl, RadarrApiKey, TimeSpan.FromMinutes(3));

            // Advertise the stack to Stackarr jobs via a StackarrInstance whose container names
            // resolve to the fixture's container aliases.
            System.Collections.Generic.Dictionary<string, string?> config = new()
            {
                ["StackName"] = StackName
            };
            Instance = new(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(config)
                .Build());

            Ready = true;
            return true;
        }
        catch
        {
            await DisposeAsync();
            return false;
        }
    }

    async Task<string> StartArr(string image, string name, int containerPort, int hostPort, Dictionary<string, string>? extraEnv = null)
    {
        await DockerHelper.PullImage(image);
        Dictionary<string, string> env = new()
        {
            ["PUID"] = "1000",
            ["PGID"] = "1000",
            ["TZ"] = "Etc/UTC"
        };
        if (extraEnv is not null)
        {
            foreach (KeyValuePair<string, string> kvp in extraEnv)
                env[kvp.Key] = kvp.Value;
        }
        return await StartContainer(image, name, containerPort, hostPort, env);
    }

    async Task<string> StartTransmission(string name, int containerPort, int hostPort)
    {
        await DockerHelper.PullImage("lscr.io/linuxserver/transmission:latest");
        return await StartContainer(
            "lscr.io/linuxserver/transmission:latest", name, containerPort, hostPort,
            env: new Dictionary<string, string>
            {
                ["PUID"] = "1000",
                ["PGID"] = "1000",
                ["TZ"] = "Etc/UTC",
                ["USER"] = "transmission",
                ["PASS"] = "stackarr",
                ["RPC_USERNAME"] = "transmission",
                ["RPC_PASSWORD"] = "stackarr",
                ["RPC_WHITELIST_ENABLED"] = "false",
                ["RPC_AUTHENTICATION_REQUIRED"] = "true",
                ["TRANSMISSION_RPC_BIND_ADDRESS"] = "0.0.0.0"
            });
    }

    async Task<string> StartContainer(string image, string name, int containerPort, int hostPort, Dictionary<string, string> env)
    {
        using DockerClient client = DockerHelper.CreateClient();

        CreateContainerParameters parameters = new()
        {
            Image = image,
            Name = name,
            Env = env.Select(e => $"{e.Key}={e.Value}").ToList(),
            Labels = new Dictionary<string, string> { ["stackarr.test"] = "true" },
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
                    [NetworkId] = new EndpointSettings { Aliases = [name] }
                }
            }
        };

        CreateContainerResponse response = await client.Containers.CreateContainerAsync(parameters);
        await client.Containers.StartContainerAsync(response.ID, new());
        return response.ID;
    }

    static async Task EnsureImages()
    {
        foreach (string image in new[]
        {
            "lscr.io/linuxserver/sonarr:latest",
            "lscr.io/linuxserver/radarr:latest",
            "lscr.io/linuxserver/prowlarr:latest"
        })
        {
            await DockerHelper.PullImage(image);
        }
    }

    async Task RemoveStaleContainers()
    {
        foreach (string name in new[] { SonarrHost, RadarrHost, ProwlarrHost, TransmissionHost })
        {
            try
            {
                using DockerClient client = DockerHelper.CreateClient();
                ContainerListResponse? existing = (await client.Containers.ListContainersAsync(
                    new ContainersListParameters { All = true })).FirstOrDefault(c => c.Names.Contains($"/{name}"));
                if (existing != null)
                    await client.Containers.RemoveContainerAsync(existing.ID,
                        new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
            }
            catch { }
        }
    }

    static async Task WaitForQualityProfiles(string baseUrl, string apiKey, TimeSpan timeout)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                Stackarr.Tvarr.Clients.RadarrClient probe = new(baseUrl, apiKey);
                if ((await probe.GetQualityProfiles()).Count > 0)
                    return;
            }
            catch { }
            await Task.Delay(2000);
        }
    }

    static async Task<bool> WaitForHttp(int port, string path, TimeSpan timeout)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
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

    static async Task<string> ApiKeyOr(string containerId, string fallback)
    {
        string xml = await ExecInContainer(containerId, "cat /config/config.xml");
        Match match = Regex.Match(xml, @"<ApiKey>([^<]+)</ApiKey>");
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value
            : fallback;
    }

    public static async Task<string> ExecInContainer(string containerId, string command)
    {
        using DockerClient client = DockerHelper.CreateClient();
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

    static int GetAvailablePort()
    {
        System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (string id in new[] { SonarrContainerId, RadarrContainerId, ProwlarrContainerId, TransmissionContainerId })
        {
            if (!string.IsNullOrEmpty(id))
                await DockerHelper.RemoveContainer(id);
        }
        if (!string.IsNullOrEmpty(NetworkId))
            await DockerHelper.DeleteNetwork(NetworkId);
    }
}
