using Stackarr.TestSupport;

namespace Stackarr.Notifications.Tests;

/// <summary>
/// A real HTTP server container (mendhak/http-https-echo) that echoes and logs every request,
/// including the parsed JSON body. Tests read its logs to assert the exact webhook payload that
/// was sent on the wire, no fake HTTP handlers involved.
/// </summary>
public sealed class EchoServer : IAsyncDisposable
{
    public const string Image = "mendhak/http-https-echo:latest";

    public static readonly string ContainerName = $"stackarr-test-notif-echo-{TestRun.Id}";

    public string ContainerId { get; private set; } = "";
    public int Port { get; private set; }
    public string BaseUrl => $"http://localhost:{Port}";
    public bool Ready { get; private set; }

    public async Task<bool> StartAsync()
    {
        if (!await DockerTestContainer.IsDockerAvailable())
            return false;

        await DockerTestContainer.RemoveContainerByName(ContainerName);
        await DockerTestContainer.EnsureImage(Image);

        (ContainerId, Port) = await DockerTestContainer.Start(
            Image, ContainerName, null, 8080);

        if (!await DockerTestContainer.WaitForHttp(Port, "/", TimeSpan.FromMinutes(2)))
            return false;

        Ready = true;
        return true;
    }

    /// <summary>Polls the container logs until the given text appears, or the timeout elapses.</summary>
    public async Task<string> WaitForLogAsync(string contains, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        string logs = "";
        while (DateTime.UtcNow < deadline)
        {
            logs = await DockerTestContainer.GetLogs(ContainerId);
            if (logs.Contains(contains, StringComparison.Ordinal))
                return logs;
            await Task.Delay(250);
        }

        return logs;
    }

    public async ValueTask DisposeAsync()
    {
        await DockerTestContainer.RemoveContainer(ContainerId);
        ContainerId = "";
    }
}

/// <summary>
/// A real HTTP server container (mccutchen/go-httpbin) used for status-code behaviour
/// (e.g. 429 rate limiting) that the echo container cannot express.
/// </summary>
public sealed class HttpBinServer : IAsyncDisposable
{
    public const string Image = "mccutchen/go-httpbin:latest";

    public static readonly string ContainerName = $"stackarr-test-notif-httpbin-{TestRun.Id}";

    public string ContainerId { get; private set; } = "";
    public int Port { get; private set; }
    public string BaseUrl => $"http://localhost:{Port}";

    public async Task<bool> StartAsync()
    {
        if (!await DockerTestContainer.IsDockerAvailable())
            return false;

        await DockerTestContainer.RemoveContainerByName(ContainerName);
        await DockerTestContainer.EnsureImage(Image);

        (ContainerId, Port) = await DockerTestContainer.Start(
            Image, ContainerName, null, 8080);

        if (!await DockerTestContainer.WaitForHttp(Port, "/get", TimeSpan.FromMinutes(2)))
            return false;

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await DockerTestContainer.RemoveContainer(ContainerId);
        ContainerId = "";
    }
}

/// <summary>
/// Shares the HTTP containers across the test class and owns session teardown. Tests skip
/// themselves when Docker is unavailable.
/// </summary>
public static class NotificationServers
{
    public const string ParallelKey = "docker";

    static readonly SemaphoreSlim Lock = new(1, 1);
    static EchoServer? _echo;
    static HttpBinServer? _httpBin;

    public static async Task<EchoServer?> GetEchoAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (_echo is not null)
                return _echo;

            EchoServer server = new();
            if (!await server.StartAsync())
            {
                await server.DisposeAsync();
                return null;
            }

            _echo = server;
            return _echo;
        }
        finally
        {
            Lock.Release();
        }
    }

    public static async Task<HttpBinServer?> GetHttpBinAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (_httpBin is not null)
                return _httpBin;

            HttpBinServer server = new();
            if (!await server.StartAsync())
            {
                await server.DisposeAsync();
                return null;
            }

            _httpBin = server;
            return _httpBin;
        }
        finally
        {
            Lock.Release();
        }
    }

    [After(TestSession)]
    public static async Task TearDown()
    {
        if (_echo is not null)
        {
            await _echo.DisposeAsync();
            _echo = null;
        }

        if (_httpBin is not null)
        {
            await _httpBin.DisposeAsync();
            _httpBin = null;
        }
    }
}
