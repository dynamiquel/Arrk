using Microsoft.Extensions.Configuration;
using Stackarr.Bazarr;
using Stackarr.Core.Services;
using Stackarr.Jellyfin;
using Stackarr.Music;
using Stackarr.Navidrome;
using Stackarr.Prowlarr;
using Stackarr.Tvarr;

namespace Stackarr.Orchestration.Tests;

/// <summary>
/// Behaviour of the <see cref="ServiceRegistry"/>: key lookups, enable/disable filtering,
/// and infrastructure classification. Per-service spec content lives in
/// <see cref="EveryServiceTests"/> and <see cref="TestServiceSpec"/>-based unit tests.
/// </summary>
public class ServiceRegistryTests
{
    [Test]
    public async Task CreateDefault_ContainsAllExpectedServices()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();

        await Assert.That(registry.Contains(TvServiceDefinition.StaticKey)).IsTrue();
        await Assert.That(registry.Contains(Stackarr.Slskd.SlskdServiceDefinition.StaticKey)).IsTrue();
        await Assert.That(registry.Contains(JellyfinServiceDefinition.StaticKey)).IsTrue();
        await Assert.That(registry.Contains(NavidromeServiceDefinition.StaticKey)).IsTrue();
        await Assert.That(registry.Contains("reverse_proxy")).IsTrue();
        await Assert.That(registry.Contains("cloudflared")).IsTrue();
        await Assert.That(registry.Contains(ProwlarrServiceDefinition.StaticKey)).IsTrue();
        await Assert.That(registry.Contains(Stackarr.Transmission.TransmissionServiceDefinition.StaticKey)).IsTrue();
        await Assert.That(registry.Contains(BazarrServiceDefinition.StaticKey)).IsTrue();
        await Assert.That(registry.Contains("vpn")).IsTrue();
    }

    [Test]
    public async Task GetRequired_ReturnsService_OrThrows()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();

        await Assert.That(registry.GetRequired("tv").Definition.Key).IsEqualTo("tv");
        await Assert.That(registry.Get("tv")).IsNotNull();
        await Assert.That(registry.Get("nonexistent")).IsNull();
        await Assert.That(() => registry.GetRequired("nonexistent")).Throws<KeyNotFoundException>();
    }

    [Test]
    public async Task GetInfrastructure_ReturnsOnlyInfrastructureServices()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();

        List<Service> infrastructure = registry.GetInfrastructure().ToList();

        await Assert.That(infrastructure).IsNotEmpty();
        foreach (Service service in infrastructure)
        {
            await Assert.That(service.Definition.IsInfrastructure).IsTrue();
            await Assert.That(service.Definition.IsEnabled(TestHelper.EmptyConfiguration())).IsTrue();
        }
    }

    [Test]
    public async Task GetNonInfrastructure_RespectsExplicitDisable()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(
            tvEnabled: false, musicEnabled: false,
            jellyfinEnabled: false, navidromeEnabled: false, vpnEnabled: false);

        foreach (Service service in registry.GetNonInfrastructure())
        {
            await Assert.That(service.Definition.IsEnabled(configuration)).IsFalse();
        }
    }

    [Test]
    public async Task InfrastructureServices_StayEnabled_WhenEverythingDisabled()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(
            tvEnabled: false, musicEnabled: false,
            jellyfinEnabled: false, navidromeEnabled: false, vpnEnabled: false);

        foreach (Service service in registry.GetInfrastructure())
        {
            await Assert.That(service.Definition.IsEnabled(configuration)).IsTrue();
        }
    }

    [Test]
    public async Task GetEnabledKeys_RespectsToggles()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(
            tvEnabled: true, musicEnabled: false,
            jellyfinEnabled: true, navidromeEnabled: false, vpnEnabled: false);

        IReadOnlyList<string> keys = registry.GetEnabledKeys(configuration);

        await Assert.That(keys).Contains("tv");
        await Assert.That(keys).Contains("jellyfin");
        await Assert.That(keys).Contains("reverse_proxy");
        await Assert.That(keys).Contains("indexer");
        await Assert.That(keys).Contains("torrent_downloader");
        await Assert.That(keys).DoesNotContain("bazarr");
        await Assert.That(keys).DoesNotContain("slskd");
        await Assert.That(keys).DoesNotContain("navidrome");
        await Assert.That(keys).DoesNotContain("vpn");
    }

    [Test]
    public async Task GetDisabledKeys_ExcludesInfrastructure()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(
            tvEnabled: false, musicEnabled: false,
            jellyfinEnabled: false, navidromeEnabled: true, vpnEnabled: false);

        IReadOnlyList<string> keys = registry.GetDisabledKeys(configuration);

        await Assert.That(keys).Contains("tv");
        await Assert.That(keys).Contains("slskd");
        await Assert.That(keys).Contains("jellyfin");
        await Assert.That(keys).Contains("vpn");

        await Assert.That(keys).DoesNotContain("reverse_proxy");
        await Assert.That(keys).DoesNotContain("indexer");
        await Assert.That(keys).DoesNotContain("torrent_downloader");
        await Assert.That(keys).DoesNotContain("navidrome");
    }

    [Test]
    public async Task FindServiceByContainerLabel_ReturnsNullForUnknown()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();

        await Assert.That(registry.FindServiceByContainerLabel("unknown-key")).IsNull();
    }

    [Test]
    public async Task Register_OverwritesByKey_AndAppendsToOrderedList()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();
        int before = registry.All.Count;

        registry.Register(new Service(new Stackarr.Core.Services.TestServiceSpec
        {
            Key = "tv",
            DisplayName = "Replacement TV",
            Description = "replacement",
            IsEnabled = _ => true,
            BuildContainerSpecs = (_, _, _) => []
        }));

        await Assert.That(registry.GetRequired("tv").Definition.DisplayName).IsEqualTo("Replacement TV");
        // Ordered list append semantics are preserved for display ordering.
        await Assert.That(registry.All.Count).IsEqualTo(before + 1);
    }

}
