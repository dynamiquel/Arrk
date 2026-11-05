using Microsoft.Extensions.Configuration;
using MudBlazor;
using Stackarr.Core.Configuration;
using Stackarr.Speedtest.Components.Shared;
using Stackarr.Ui;

namespace Stackarr.Speedtest;

/// <summary>
/// Contributes the network speedtest tile and, when a VPN tunnel is required, the VPN speedtest
/// tile. The tunnel flag is read at startup like the rest of the module's contributions.
/// </summary>
public sealed class SpeedtestTileProvider : ITileProvider
{
    public SpeedtestTileProvider(IConfiguration configuration)
    {
        List<TileDefinition> tiles =
        [
            new(
                "system.speedtest",
                "System",
                "Speedtest",
                Icons.Material.Filled.Speed,
                typeof(SpeedtestTile),
                Order: 70,
                Description: "Latest network download, upload and ping results.",
                DefaultSection: "Overview",
                DefaultOrder: 90,
                Parameters: new Dictionary<string, object?> { ["Target"] = "network" })
        ];

        if (configuration.IsVpnTunnelRequired())
        {
            tiles.Add(new(
                "system.speedtest.vpn",
                "System",
                "VPN Speedtest",
                Icons.Material.Filled.VpnLock,
                typeof(SpeedtestTile),
                Order: 71,
                Description: "Latest speedtest through the VPN tunnel.",
                DefaultSection: "Overview",
                DefaultOrder: 91,
                Parameters: new Dictionary<string, object?> { ["Target"] = "vpn" }));
        }

        Tiles = tiles;
    }

    public IReadOnlyList<TileDefinition> Tiles { get; }
}
