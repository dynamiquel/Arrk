using Microsoft.Extensions.Configuration;
using Stackarr.Configuration;
using Stackarr.Core.Configuration;
using Stackarr.Prowlarr.Configuration;
using Stackarr.Tvarr;

namespace Stackarr.Orchestration.Tests;

/// <summary>
/// Verifies the reflection-free <see cref="ConfigRenderer"/> emits a complete,
/// camelCase YAML document for the registered sections.
/// </summary>
public class ConfigRendererTests
{
    static ConfigSectionRegistry BuildRegistry(bool includeIndexers = false)
    {
        ConfigSectionRegistry registry = new ConfigSectionRegistry();
        ConfigDefaults.RegisterCoreSections(registry);
        if (includeIndexers)
            registry.Register(new ConfigSectionProvider<ProwlarrConfig>("indexers", "Indexers"));
        return registry;
    }

    static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public async Task Render_DefaultCoreConfig_ContainsAllSections()
    {
        string yaml = ConfigRenderer.Render(BuildConfiguration(new()), BuildRegistry());

        await Assert.That(yaml).Contains("storage:");
        await Assert.That(yaml).Contains("vpn:");
        await Assert.That(yaml).Contains("routes:");
        await Assert.That(yaml).Contains("notifications:");
        await Assert.That(yaml).Contains("monitoring:");
        await Assert.That(yaml).Contains("qualityProfiles:");
    }

    [Test]
    public async Task Render_StorageOverrides_AreApplied()
    {
        IConfiguration configuration = BuildConfiguration(new()
        {
            ["storage:media"] = "/data/media",
            ["storage:downloads"] = "/data/downloads"
        });

        string yaml = ConfigRenderer.Render(configuration, BuildRegistry());

        await Assert.That(yaml).Contains("media: /data/media");
        await Assert.That(yaml).Contains("downloads: /data/downloads");
    }

    [Test]
    public async Task Render_VpnEnabled_ShowsWireguardDetails()
    {
        IConfiguration configuration = BuildConfiguration(new()
        {
            ["vpn:enabled"] = "true",
            ["vpn:wireguardPrivateKey"] = "secret-key",
            ["vpn:wireguardAddresses"] = "10.0.0.1/32"
        });

        string yaml = ConfigRenderer.Render(configuration, BuildRegistry());

        await Assert.That(yaml).Contains("wireguardPrivateKey: secret-key");
        await Assert.That(yaml).Contains("wireguardAddresses: 10.0.0.1/32");
    }

    [Test]
    public async Task Render_IndexersWithEntries_ShowsEntries()
    {
        IConfiguration configuration = BuildConfiguration(new()
        {
            ["indexers:entries:0:name"] = "1337x",
            ["indexers:entries:0:implementation"] = "Indexer1337x",
            ["indexers:entries:0:enabled"] = "true"
        });

        string yaml = ConfigRenderer.Render(configuration, BuildRegistry(includeIndexers: true));

        await Assert.That(yaml).Contains("name: 1337x");
        await Assert.That(yaml).Contains("implementation: Indexer1337x");
    }

    [Test]
    public async Task Render_ServiceOverrides_AreApplied()
    {
        ConfigSectionRegistry registry = BuildRegistry();
        registry.Register(new ConfigSectionProvider<TvConfig>("services:tv", "TV"));

        IConfiguration configuration = BuildConfiguration(new()
        {
            ["services:tv:enabled"] = "false"
        });

        string yaml = ConfigRenderer.Render(configuration, registry);

        await Assert.That(yaml).Contains("services:");
        await Assert.That(yaml).Contains("enabled: false");
    }
}
