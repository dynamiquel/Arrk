using Arrk.TestSupport;

namespace Arrk.Prowlarr.Tests;

/// <summary>
/// A real Prowlarr container used by <see cref="ProwlarrIntegrationTests"/>. It shares a per-run
/// Docker network with an nginx stub that speaks the FlareSolverr protocol, so Prowlarr's
/// indexer-proxy validation (which refuses a proxy whose host does not answer) can be satisfied
/// without a heavyweight browser-based solver.
/// </summary>
public sealed class ProwlarrServer : IAsyncDisposable
{
    public static readonly string ContainerName = $"stackarr-test-prowlarr-{TestRun.Id}";
    public static readonly string SolverContainerName = $"stackarr-test-solver-{TestRun.Id}";

    /// <summary>Host URL for the stub solver, resolvable from the Prowlarr container.</summary>
    public string SolverHost => $"http://{SolverContainerName}:8191/";

    public string ContainerId { get; private set; } = "";
    public string SolverContainerId { get; private set; } = "";
    public string? NetworkId { get; private set; }
    public int Port { get; private set; }
    public string ApiKey { get; private set; } = "";
    public bool Ready { get; private set; }

    string _solverConfigPath = "";

    public string Url => $"http://localhost:{Port}";

    /// <summary>A minimal FlareSolverr response Prowlarr accepts as a successful proxy test.</summary>
    const string FlareSolverrStubConfig = """
    server {
        listen 8191;
        location /v1 {
            default_type application/json;
            return 200 '{"status":"ok","message":"ok","solution":{"url":"https://prowlarr.servarr.com/v1/ping","status":200,"headers":{},"response":"OK","cookies":[],"userAgent":"stackarr-test"},"startTimestamp":0,"endTimestamp":0,"version":"3.3.21"}';
        }
        location /health {
            default_type application/json;
            return 200 '{"msg":"ok"}';
        }
        location / { return 404; }
    }
    """;

    public async Task<bool> StartAsync()
    {
        if (!await DockerTestContainer.IsDockerAvailable())
            return false;

        await DockerTestContainer.RemoveContainerByName(ContainerName);
        await DockerTestContainer.RemoveContainerByName(SolverContainerName);
        await DockerTestContainer.EnsureImage("lscr.io/linuxserver/prowlarr:latest");

        NetworkId = await DockerTestContainer.CreateNetwork($"stackarr-test-prowlarrnet-{TestRun.Id}");

        (ContainerId, Port) = await DockerTestContainer.Start(
            "lscr.io/linuxserver/prowlarr:latest",
            ContainerName,
            NetworkId,
            9696,
            new Dictionary<string, string>
            {
                ["PUID"] = "1000",
                ["PGID"] = "1000",
                ["TZ"] = "Etc/UTC"
            });

        if (!await DockerTestContainer.WaitForHttp(Port, "/api/v1/system/status", TimeSpan.FromMinutes(4)))
            return false;

        SolverContainerId = await StartSolverAsync();

        ApiKey = await DockerTestContainer.ReadApiKey(ContainerId);
        Ready = !string.IsNullOrEmpty(ApiKey);
        return Ready;
    }

    async Task<string> StartSolverAsync()
    {
        await DockerTestContainer.EnsureImage("nginx:alpine");

        string configPath = Path.Combine(Path.GetTempPath(), $"stackarr-solver-{TestRun.Id}.conf");
        await File.WriteAllTextAsync(configPath, FlareSolverrStubConfig);
        _solverConfigPath = configPath;

        (string containerId, int hostPort) = await DockerTestContainer.Start(
            "nginx:alpine",
            SolverContainerName,
            NetworkId,
            8191,
            binds: [$"{configPath}:/etc/nginx/conf.d/default.conf:ro"]);

        await DockerTestContainer.WaitForHttp(hostPort, "/health", TimeSpan.FromSeconds(60));
        return containerId;
    }

    public ProwlarrClient CreateClient() => new(Url, ApiKey);

    public async ValueTask DisposeAsync()
    {
        await DockerTestContainer.RemoveContainer(SolverContainerId);
        await DockerTestContainer.RemoveContainer(ContainerId);

        if (!string.IsNullOrEmpty(_solverConfigPath))
        {
            try { File.Delete(_solverConfigPath); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Shares a single <see cref="ProwlarrServer"/> across the test class and owns the global teardown
/// hook (TUnit prefers global hooks in their own class rather than mixed into a test class).
/// </summary>
public static class ProwlarrServerShared
{
    public const string ParallelKey = "docker";

    static readonly SemaphoreSlim Lock = new(1, 1);
    static ProwlarrServer? _server;

    public static async Task<ProwlarrServer?> GetServerAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (_server is not null) return _server;
            ProwlarrServer server = new();
            if (!await server.StartAsync())
            {
                await server.DisposeAsync();
                return null;
            }
            _server = server;
            return _server;
        }
        finally
        {
            Lock.Release();
        }
    }

    [After(TestSession)]
    public static async Task TearDown()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
