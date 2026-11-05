using Docker.DotNet;

namespace Stackarr.Docker;

public static class DockerClientFactory
{
    public static DockerClient Create()
    {
        string socketPath = Environment.GetEnvironmentVariable("DOCKER_SOCKET") ?? "/var/run/docker.sock";
        return new DockerClientConfiguration(new Uri($"unix://{socketPath}")).CreateClient();
    }
}
