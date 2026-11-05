using Microsoft.Extensions.Configuration;
using Stackarr.Core.Configuration;

namespace Stackarr.Configuration.Tests;

public class ConfigurationExtensionsTests
{
    static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    [Test]
    public async Task IsServiceVpnOptIn_DefaultsFalse()
    {
        await Assert.That(Config().IsServiceVpnOptIn("services:torrentDownloader")).IsFalse();
        await Assert.That(Config(("services:torrentDownloader:vpn", "true")).IsServiceVpnOptIn("services:torrentDownloader")).IsTrue();
    }

    [Test]
    public async Task IsTorrentVpnRouted_TrueWhenOptedIn_EvenWithoutMasterSwitch()
    {
        await Assert.That(Config(("services:torrentDownloader:vpn", "true")).IsTorrentVpnRouted()).IsTrue();
        await Assert.That(Config(("vpn:enabled", "true")).IsTorrentVpnRouted()).IsFalse();
        await Assert.That(Config().IsTorrentVpnRouted()).IsFalse();
    }

    [Test]
    public async Task IsVpnTunnelRequired_TrueWhenMasterOrOptIn()
    {
        await Assert.That(Config(("vpn:enabled", "true")).IsVpnTunnelRequired()).IsTrue();
        await Assert.That(Config(("services:torrentDownloader:vpn", "true")).IsVpnTunnelRequired()).IsTrue();
        await Assert.That(Config(("services:slskd:vpn", "true")).IsVpnTunnelRequired()).IsTrue();
        await Assert.That(Config().IsVpnTunnelRequired()).IsFalse();
    }

    [Test]
    public async Task IsSlskdVpnRouted_TrueOnlyWhenOptedIn()
    {
        await Assert.That(Config(("services:slskd:vpn", "true")).IsSlskdVpnRouted()).IsTrue();
        await Assert.That(Config(("vpn:enabled", "true")).IsSlskdVpnRouted()).IsFalse();
        await Assert.That(Config().IsSlskdVpnRouted()).IsFalse();
    }

    [Test]
    public async Task IsServiceProxied_UsesProvidedDefault()
    {
        await Assert.That(Config().IsServiceProxied("services:torrentDownloader", defaultValue: true)).IsTrue();
        await Assert.That(Config().IsServiceProxied("services:torrentDownloader", defaultValue: false)).IsFalse();
        await Assert.That(Config(("services:torrentDownloader:proxy", "true")).IsServiceProxied("services:torrentDownloader", defaultValue: false)).IsTrue();
    }
}
