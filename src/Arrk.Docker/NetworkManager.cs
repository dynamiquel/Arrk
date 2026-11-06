using Docker.DotNet;
using Docker.DotNet.Models;

namespace Arrk.Docker;

public class NetworkManager(DockerClient client)
{
    public async Task<string> CreateOrGetNetwork(string networkName, Dictionary<string, string>? labels = null, CancellationToken ct = default)
    {
        NetworksListParameters networksListParameters = new();
        IList<NetworkResponse>? networks = await client.Networks.ListNetworksAsync(networksListParameters, ct);
        NetworkResponse? existing = networks.FirstOrDefault(n => n.Name == networkName);
        if (existing != null)
            return existing.ID;

        try
        {
            NetworksCreateParameters createParams = new()
            {
                Name = networkName,
                Driver = "bridge",
                CheckDuplicate = true,
                Labels = labels
            };
            NetworksCreateResponse? response = await client.Networks.CreateNetworkAsync(createParams, ct);

            return response.ID;
        }
        catch (DockerApiException)
        {
            networksListParameters = new();
            networks = await client.Networks.ListNetworksAsync(networksListParameters, ct);
            existing = networks.FirstOrDefault(n => n.Name == networkName);
            if (existing != null)
                return existing.ID;
            throw;
        }
    }

    public async Task ConnectContainer(string networkId, string containerId, CancellationToken ct = default)
    {
        NetworkConnectParameters connectParams = new()
        {
            Container = containerId
        };
        await client.Networks.ConnectNetworkAsync(networkId, connectParams, ct);
    }

    public async Task DisconnectContainer(string networkId, string containerId, CancellationToken ct = default)
    {
        NetworkDisconnectParameters disconnectParams = new()
        {
            Container = containerId,
            Force = true
        };
        await client.Networks.DisconnectNetworkAsync(networkId, disconnectParams, ct);
    }

    public async Task<bool> NetworkExists(string networkName, CancellationToken ct = default)
    {
        NetworksListParameters networksListParameters = new();
        IList<NetworkResponse>? networks = await client.Networks.ListNetworksAsync(networksListParameters, ct);
        return networks.Any(n => n.Name == networkName);
    }

    public async Task<List<NetworkResponse>> ListNetworks(CancellationToken ct = default)
    {
        NetworksListParameters networksListParameters = new();
        IList<NetworkResponse>? networks = await client.Networks.ListNetworksAsync(networksListParameters, ct);
        return networks.ToList();
    }

    public async Task DeleteNetwork(string networkId, CancellationToken ct = default)
    {
        await client.Networks.DeleteNetworkAsync(networkId, ct);
    }
}
