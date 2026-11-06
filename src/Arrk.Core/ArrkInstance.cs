using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Arrk.Core.Configuration;
using Arrk.Core.Models;

namespace Arrk.Core;

public class ArrkInstance
{
    public string StackName { get; }

    // Persisted Docker names also identify service data volumes; branding must not orphan them.
    public string ContainerPrefix => $"stackarr-{StackName}-";

    /// <summary>
    /// The Docker network every container in this Arrk instance joins. It is scoped to the
    /// stack name so multiple Arrk instances can run side by side on one host without their
    /// reverse proxies discovering each other's containers (Traefik router names are fixed, so a
    /// shared network would make the two instances collide). Tests can supply an explicit name
    /// so a whole run shares one network instead of creating one per instance.
    /// </summary>
    public string NetworkName { get; }

    /// <summary>
    /// The name of Arrk's own container when running inside Docker, or <c>null</c> when
    /// running standalone. Used to route the web UI through the reverse proxy.
    /// </summary>
    public string? SelfContainerName { get; }

    public ArrkInstance(IConfiguration configuration, string? configDir = null, string? networkName = null)
    {
        string? stackName = configuration.GetValue<string?>("StackName");
        string? selfContainerName = GetContainerNameFromDocker();

        if (string.IsNullOrWhiteSpace(stackName))
        {
            stackName = selfContainerName ?? GenerateRandomId();
            configuration["StackName"] = stackName;
            TryPersistStackName(stackName, configDir);
        }

        StackName = stackName;
        SelfContainerName = selfContainerName;
        NetworkName = string.IsNullOrWhiteSpace(networkName) ? $"stackarr-{StackName}-net" : networkName;
    }

    public string GetContainerName(string implementationName) =>
        $"{ContainerPrefix}{implementationName}";

    /// <summary>
    /// Docker network mode that makes a container share Gluetun's network namespace. The Docker
    /// daemon expects <c>container:&lt;name|id&gt;</c> (the Compose <c>service:&lt;name&gt;</c>
    /// form is resolved by Compose before it reaches the daemon).
    /// </summary>
    public string GetVpnNetworkMode() =>
        $"container:{ContainerPrefix}gluetun";

    /// <summary>Name of the Gluetun container that VPN-routed services share a namespace with.</summary>
    public string GetVpnContainerName() => GetContainerName("gluetun");

    /// <summary>
    /// Resolves the host other containers (and Arrk's own clients) should use to reach a
    /// service. A VPN-routed service has no DNS record of its own on the Docker network, its
    /// ports live in Gluetun's network namespace, so it must be addressed via the Gluetun
    /// container name.
    /// </summary>
    public string ResolveServiceHost(string containerName, bool vpnRouted) =>
        vpnRouted ? GetVpnContainerName() : containerName;

    static string GenerateRandomId()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(4);
        uint value = BitConverter.ToUInt32(bytes) % 100_000_000;
        return value.ToString("D8");
    }

    static string? GetContainerNameFromDocker()
    {
        try
        {
            string? containerId = Environment.GetEnvironmentVariable("HOSTNAME");
            if (string.IsNullOrWhiteSpace(containerId))
                return null;

            string socketPath = Environment.GetEnvironmentVariable("DOCKER_SOCKET") ?? "/var/run/docker.sock";

            using var handler = new SocketsHttpHandler();
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            };

            using var client = new HttpClient(handler);
            using var response = client.GetAsync($"http://localhost/containers/{containerId}/json").GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
                return null;

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Name", out JsonElement nameProp))
            {
                string? name = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    return name.TrimStart('/');
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    static void TryPersistStackName(string stackName, string? configDir)
    {
        if (string.IsNullOrEmpty(configDir))
            return;

        try
        {
            string yamlPath = ArrkFiles.FilePath(configDir, ArrkFiles.ConfigFileName);
            string jsonPath = ArrkFiles.FilePath(configDir, ArrkFiles.ConfigJsonFileName);

            if (File.Exists(yamlPath))
                PersistToYaml(yamlPath, stackName);
            else if (File.Exists(jsonPath))
                PersistToJsonProperty(jsonPath, "StackName", stackName);
        }
        catch
        {
            // Best-effort persistence; the in-memory config already has the value
        }
    }

    static void PersistToYaml(string path, string stackName)
    {
        string[] lines = File.ReadAllLines(path);
        bool found = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("stackName:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"stackName: {stackName}";
                found = true;
                break;
            }
        }

        if (!found)
        {
            List<string> newLines = [.. lines, $"stackName: {stackName}"];
            lines = [.. newLines];
        }

        File.WriteAllLines(path, lines);
    }

    static void PersistToJsonProperty(string path, string property, string value)
    {
        string json = File.ReadAllText(path);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);

        var dict = doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetRawText());

        dict[property] = $"\"{value}\"";

        string updated = System.Text.Json.JsonSerializer.Serialize(dict,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, updated);
    }
}
