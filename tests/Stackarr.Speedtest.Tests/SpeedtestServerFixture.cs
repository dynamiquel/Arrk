using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Speedtest.Models;
using Stackarr.TestSupport;

namespace Stackarr.Speedtest.Tests;

/// <summary>
/// A real nginx container configured to emulate the Cloudflare speed endpoints, used to drive the
/// real <see cref="CloudflareSpeedtestClient"/> against a real HTTP server.
/// </summary>
public sealed class SpeedtestServer : IAsyncDisposable
{
    public static readonly string ContainerName = $"stackarr-test-speedtest-{TestRun.Id}";

    public string ContainerId { get; private set; } = "";
    public int Port { get; private set; }
    public string BaseUrl => $"http://localhost:{Port}";
    public bool Ready { get; private set; }

    public async Task<bool> StartAsync()
    {
        if (!await DockerTestContainer.IsDockerAvailable())
            return false;

        await DockerTestContainer.RemoveContainerByName(ContainerName);
        await DockerTestContainer.EnsureImage("nginx:alpine");

        (ContainerId, Port) = await DockerTestContainer.Start(
            "nginx:alpine", ContainerName, null, 80);

        if (!await DockerTestContainer.WaitForHttp(Port, "/", TimeSpan.FromMinutes(2)))
            return false;

        // Install a deterministic config emulating the Cloudflare speed endpoints.
        const string conf = """
            server {
                listen 80;
                server_name _;
                location /__down {
                    add_header Server-Timing 'cfRequestDuration;dur=1.5';
                    default_type application/octet-stream;
                    return 200 '0123456789';
                }
                location /__up {
                    add_header Server-Timing 'cfRequestDuration;dur=2.0';
                    default_type application/octet-stream;
                    return 200 'ok';
                }
                location / {
                    return 200 'ok';
                }
            }
            """;

        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(conf));
        await DockerTestContainer.Exec(ContainerId,
            $"echo {encoded} | base64 -d > /etc/nginx/conf.d/default.conf && nginx -s reload");

        await Task.Delay(500);
        Ready = true;
        return true;
    }

    public CloudflareSpeedtestClient CreateClient()
    {
        HttpClient http = new();
        return new CloudflareSpeedtestClient(http, NullLogger<CloudflareSpeedtestClient>.Instance)
        {
            DownloadUrl = $"{BaseUrl}/__down",
            UploadUrl = $"{BaseUrl}/__up",
            Profile = SpeedtestProfile.Fast
        };
    }

    public async ValueTask DisposeAsync()
    {
        await DockerTestContainer.RemoveContainer(ContainerId);
    }
}

/// <summary>
/// Shares a single <see cref="SpeedtestServer"/> and owns the global teardown hook (TUnit prefers
/// global hooks in their own class rather than mixed into a test class).
/// </summary>
public static class SpeedtestServerShared
{
    public const string ParallelKey = "docker";

    static readonly SemaphoreSlim Lock = new(1, 1);
    static SpeedtestServer? _server;

    public static async Task<SpeedtestServer?> GetServerAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (_server is not null)
                return _server;

            SpeedtestServer server = new();
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
