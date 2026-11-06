using Microsoft.Extensions.Configuration;
using Arrk.Speedtest.Components.Shared;
using Arrk.Ui;

namespace Arrk.Speedtest.Tests;

/// <summary>
/// The speedtest module contributes its tiles to the shared catalogue: the network tile always, and
/// the VPN tile only when the configuration requires a tunnel.
/// </summary>
public class SpeedtestUiTests
{
    static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    [Test]
    public async Task Provider_ContributesTheNetworkSpeedtestTile()
    {
        SpeedtestTileProvider provider = new(Config());

        TileDefinition? network = provider.Tiles.FirstOrDefault(t => t.Id == "system.speedtest");

        await Assert.That(network).IsNotNull();
        await Assert.That(network!.Component).IsEqualTo(typeof(SpeedtestTile));
        await Assert.That(network.TitleFor(TilePlacement.Dashboard)).IsEqualTo("SYSTEM - SPEEDTEST");
    }

    [Test]
    public async Task Provider_AddsTheVpnTileOnlyWhenTheTunnelIsRequired()
    {
        SpeedtestTileProvider withoutVpn = new(Config());
        SpeedtestTileProvider withVpn = new(Config(("services:torrentDownloader:vpn", "true")));

        await Assert.That(withoutVpn.Tiles.Any(t => t.Id == "system.speedtest.vpn")).IsFalse();
        await Assert.That(withVpn.Tiles.Any(t => t.Id == "system.speedtest.vpn")).IsTrue();
    }
}
