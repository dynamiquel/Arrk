using Microsoft.Extensions.Configuration;
using Arrk.Bazarr;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Services;
using Arrk.Jellyfin;
using Arrk.Navidrome;
using Arrk.Prowlarr;
using Arrk.Tvarr;

namespace Arrk.Orchestration.Tests;

/// <summary>
/// Verifies that services default to enabled when their config section is absent.
/// </summary>
public class ConfigDefaultsTests
{
    static IConfiguration EmptyConfiguration() => TestHelper.EmptyConfiguration();

    [Test]
    public async Task ServiceDefinitions_DefaultEnabled_WhenConfigSectionAbsent()
    {
        IConfiguration emptyConfig = EmptyConfiguration();

        await Assert.That(new TvServiceDefinition().IsEnabled(emptyConfig)).IsTrue();
        await Assert.That(new Arrk.Slskd.SlskdServiceDefinition().IsEnabled(emptyConfig)).IsTrue();
        await Assert.That(new JellyfinServiceDefinition().IsEnabled(emptyConfig)).IsTrue();
        await Assert.That(new NavidromeServiceDefinition().IsEnabled(emptyConfig)).IsTrue();
    }

    [Test]
    public async Task InfrastructureServices_AlwaysEnabled()
    {
        IConfiguration emptyConfig = EmptyConfiguration();

        await Assert.That(new TraefikServiceDefinition().IsEnabled(emptyConfig)).IsTrue();
        await Assert.That(new ProwlarrServiceDefinition().IsEnabled(emptyConfig)).IsTrue();
        await Assert.That(new Arrk.Transmission.TransmissionServiceDefinition().IsEnabled(emptyConfig)).IsTrue();
    }

    [Test]
    public async Task Bazarr_DefaultDisabled_WhenConfigSectionAbsent()
    {
        IConfiguration emptyConfig = EmptyConfiguration();

        await Assert.That(new BazarrServiceDefinition().IsEnabled(emptyConfig)).IsFalse();
    }

    [Test]
    public async Task Bazarr_Enabled_WhenItsFlagIsSet()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["services:subtitles:bazarr:enabled"] = "true" })
            .Build();

        await Assert.That(new BazarrServiceDefinition().IsEnabled(config)).IsTrue();
    }

    [Test]
    public async Task VpnService_DefaultDisabled_WhenConfigSectionAbsent()
    {
        IConfiguration emptyConfig = EmptyConfiguration();

        await Assert.That(new GluetunServiceDefinition().IsEnabled(emptyConfig)).IsFalse();
    }

    [Test]
    public async Task ServicesExplicitlyDisabled_WhenConfigSaysSo()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:tv:enabled"] = "false",
                ["services:music:enabled"] = "false",
                ["services:slskd:enabled"] = "false",
                ["services:jellyfin:enabled"] = "false",
                ["services:navidrome:enabled"] = "false",
                ["vpn:enabled"] = "true"
            })
            .Build();

        await Assert.That(new TvServiceDefinition().IsEnabled(config)).IsFalse();
        await Assert.That(new Arrk.Slskd.SlskdServiceDefinition().IsEnabled(config)).IsFalse();
        await Assert.That(new JellyfinServiceDefinition().IsEnabled(config)).IsFalse();
        await Assert.That(new NavidromeServiceDefinition().IsEnabled(config)).IsFalse();
        await Assert.That(new GluetunServiceDefinition().IsEnabled(config)).IsTrue();
    }

    [Test]
    public async Task ServiceRegistry_GetEnabled_IncludesAllByDefault()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();

        List<string> enabled = registry.GetEnabled(EmptyConfiguration()).Select(s => s.Definition.Key).ToList();

        foreach (string key in new[] { "tv", "slskd", "jellyfin", "navidrome", "indexer", "torrent_downloader", "reverse_proxy" })
        {
            await Assert.That(enabled).Contains(key);
        }
        await Assert.That(enabled).DoesNotContain("vpn");
        await Assert.That(enabled).DoesNotContain("bazarr");
    }

    [Test]
    public async Task IsServiceExtension_DefaultsToTrue_WhenSectionAbsent()
    {
        IConfiguration emptyConfig = EmptyConfiguration();

        await Assert.That(emptyConfig.IsServiceEnabled("services:tv")).IsTrue();
        await Assert.That(emptyConfig.IsServiceEnabled("services:music")).IsTrue();
    }

    [Test]
    public async Task IsServiceExtension_RespectsExplicitValue()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["services:tv:enabled"] = "false" })
            .Build();

        await Assert.That(config.IsServiceEnabled("services:tv")).IsFalse();
        await Assert.That(config.IsServiceEnabled("services:music")).IsTrue();
    }
}
