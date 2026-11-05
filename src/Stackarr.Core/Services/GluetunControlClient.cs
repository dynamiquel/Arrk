using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Stackarr.Core.Services;

/// <summary>
/// Talks to Gluetun's HTTP control server to restart the VPN tunnel in place. Stopping then
/// starting the tunnel forces a fresh provider reconnection while keeping Gluetun's network
/// namespace alive, so VPN-routed containers do not need to be recreated.
/// </summary>
public class GluetunControlClient(StackarrInstance stackarr, ILogger<GluetunControlClient> logger)
{
    /// <summary>Address of Gluetun's control server as seen from Stackarr's own network.</summary>
    public virtual string BaseAddress =>
        $"http://{stackarr.GetVpnContainerName()}:{GluetunServiceDefinition.ControlServerPort}";

    /// <summary>Stops then starts the VPN process, forcing a fresh provider reconnection.</summary>
    public virtual async Task RestartAsync(CancellationToken ct = default)
    {
        using HttpClient client = CreateClient();
        await SetStatusAsync(client, "stopped", ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await SetStatusAsync(client, "running", ct);
    }

    public virtual HttpClient CreateClient()
    {
        HttpClient client = new(CreateHandler())
        {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Add("X-API-Key", GluetunModule.ControlApiKey);
        return client;
    }

    public virtual HttpMessageHandler CreateHandler() => new HttpClientHandler();

    async Task SetStatusAsync(HttpClient client, string status, CancellationToken ct)
    {
        string json = $@"{{""status"":""{status}""}}";
        using StringContent body = new(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PutAsync("/v1/vpn/status", body, ct);

        if (!response.IsSuccessStatusCode)
            logger.LogWarning("VPN control '{Status}' returned {Code}", status, (int)response.StatusCode);
    }
}
