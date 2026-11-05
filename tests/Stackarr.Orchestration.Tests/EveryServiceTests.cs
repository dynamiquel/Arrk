using Microsoft.Extensions.Configuration;
using Stackarr.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Orchestration.Tests;

/// <summary>
/// Enforces structural conventions that every registered service/container spec must follow.
/// Complements the targeted per-service assertions in <see cref="ServiceRegistryTests"/>.
/// </summary>
public class EveryServiceTests
{
    readonly ServiceRegistry _registry = TestHelper.CreateDefault();

    static IConfiguration Config() => TestHelper.CreateServiceConfiguration();

    static (StackarrConfig Config, VolumePathResolver Resolver, StackarrInstance Stackarr) Context()
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        return (config, new VolumePathResolver(config.Storage), OrchestrationTestHelper.CreateStackarrInstance());
    }

    static IEnumerable<ContainerSpec> AllSpecs(ServiceRegistry registry, IConfiguration configuration, StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr)
    {
        foreach (Service service in registry.All)
        {
            foreach (ContainerSpec spec in service.Definition.BuildContainerSpecs(configuration, resolver, stackarr))
            {
                yield return spec;
            }
        }
    }

    [Test]
    public async Task AllServices_HaveIdentityMetadata()
    {
        foreach (Service service in _registry.All)
        {
            await Assert.That(service.Definition.Key).IsNotEmpty();
            await Assert.That(service.Definition.DisplayName).IsNotEmpty();
            await Assert.That(service.Definition.Description).IsNotEmpty();
        }
    }

    [Test]
    public async Task AllServices_ProduceAtLeastOneSpec()
    {
        IConfiguration configuration = Config();
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        foreach (Service service in _registry.All)
        {
            IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, resolver, stackarr);
            await Assert.That(specs).IsNotEmpty();
        }
    }

    [Test]
    public async Task AllContainers_FollowSpecConventions()
    {
        IConfiguration configuration = Config();
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        foreach (Service service in _registry.All)
        {
            IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, resolver, stackarr);
            foreach (ContainerSpec spec in specs)
            {
                await Assert.That(spec.Image).IsNotEmpty();
                await Assert.That(spec.Name).StartsWith("stackarr-");
                await Assert.That(spec.Labels).ContainsKey(ContainerLabels.Service);
                await Assert.That(spec.Labels[ContainerLabels.Service]).IsEqualTo(service.Definition.Key);
                await Assert.That(spec.RestartPolicy).IsEqualTo(RestartPolicyType.UnlessStopped);
                await Assert.That(spec.NetworkName).IsEqualTo(stackarr.NetworkName);
            }
        }
    }

    [Test]
    public async Task AllContainers_HaveValidVolumes()
    {
        IConfiguration configuration = Config();
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        foreach (Service service in _registry.All)
        {
            if (service.Definition.Key is "vpn" or "cloudflared") continue;

            IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, resolver, stackarr);
            foreach (ContainerSpec spec in specs)
            {
                await Assert.That(spec.Volumes).IsNotEmpty();
                foreach (VolumeMount vol in spec.Volumes)
                {
                    await Assert.That(vol.HostPath).IsNotEmpty();
                    await Assert.That(vol.ContainerPath).IsNotEmpty();
                }
            }
        }
    }

    [Test]
    public async Task AllContainerNames_AreUnique()
    {
        IConfiguration configuration = Config();
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();
        List<string> names = [];

        foreach (ContainerSpec spec in AllSpecs(_registry, configuration, config, resolver, stackarr))
        {
            await Assert.That(names).DoesNotContain(spec.Name);
            names.Add(spec.Name);
        }
    }

    [Test]
    public async Task AllTraefikConfigs_AreWellFormedAndUnique()
    {
        IConfiguration configuration = Config();
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();
        List<string> serviceNames = [];

        foreach (ContainerSpec spec in AllSpecs(_registry, configuration, config, resolver, stackarr))
        {
            if (spec.Traefik == null) continue;

            await Assert.That(spec.Traefik.Port).IsGreaterThan(0);
            await Assert.That(spec.Traefik.Port).IsLessThan(65536);
            await Assert.That(spec.Traefik.Rule).Contains("PathPrefix");
            await Assert.That(spec.Traefik.ServiceName).IsNotEmpty();
            await Assert.That(serviceNames).DoesNotContain(spec.Traefik.ServiceName);
            serviceNames.Add(spec.Traefik.ServiceName);
        }
    }

    [Test]
    public async Task AllPortMappings_HaveValidPorts()
    {
        IConfiguration configuration = Config();
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        foreach (ContainerSpec spec in AllSpecs(_registry, configuration, config, resolver, stackarr))
        {
            foreach (PortMapping port in spec.Ports)
            {
                await Assert.That(port.HostPort).IsGreaterThan(0);
                await Assert.That(port.HostPort).IsLessThan(65536);
                await Assert.That(port.ContainerPort).IsGreaterThan(0);
                await Assert.That(port.ContainerPort).IsLessThan(65536);
            }
        }
    }

    [Test]
    public async Task VpnRoutedServices_UseGluetunNetwork_AndKeepTraefikMetadata_WhenVpnEnabled()
    {
        string[] vpnServiceKeys = ["torrent_downloader"];
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
        StackarrConfig config = ConfigDefaults.GetDefaults();
        VolumePathResolver resolver = new(config.Storage);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        foreach (string key in vpnServiceKeys)
        {
            foreach (ContainerSpec spec in _registry.GetRequired(key).Definition.BuildContainerSpecs(configuration, resolver, stackarr))
            {
                // Metadata is retained; the provisioner turns it into a file-provider route
                // pointing at Gluetun instead of Docker labels.
                await Assert.That(spec.Traefik).IsNotNull();
                await Assert.That(spec.NetworkMode).IsEqualTo(stackarr.GetVpnNetworkMode());
            }
        }
    }

    [Test]
    public async Task VpnOptIn_Alone_ProvisionsTunnelAndRoutesDownloader()
    {
        // Opting the downloader in is enough: vpn.enabled may be left false.
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: false, torrentVpn: true);
        await Assert.That(new GluetunServiceDefinition().IsEnabled(configuration)).IsTrue();

        StackarrConfig config = ConfigDefaults.GetDefaults();
        VolumePathResolver resolver = new(config.Storage);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        ContainerSpec spec = _registry.GetRequired("torrent_downloader")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).Single();
        await Assert.That(spec.NetworkMode).IsEqualTo(stackarr.GetVpnNetworkMode());
    }

    [Test]
    public async Task VpnRoutedServices_UseTraefik_WhenVpnDisabled()
    {
        string[] vpnServiceKeys = ["torrent_downloader"];
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: false, torrentVpn: false);
        StackarrConfig config = ConfigDefaults.GetDefaults();
        VolumePathResolver resolver = new(config.Storage);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        foreach (string key in vpnServiceKeys)
        {
            foreach (ContainerSpec spec in _registry.GetRequired(key).Definition.BuildContainerSpecs(configuration, resolver, stackarr))
            {
                await Assert.That(spec.Traefik).IsNotNull();
                await Assert.That(spec.NetworkMode).IsNull();
            }
        }
    }

    [Test]
    public async Task NonVpnServices_AlwaysUseNormalNetwork_EvenWhenVpnEnabled()
    {
        string[] nonVpnKeys = ["indexer", "bazarr", "slskd", "jellyfin", "navidrome"];
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
        StackarrConfig config = ConfigDefaults.GetDefaults();
        VolumePathResolver resolver = new(config.Storage);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        foreach (string key in nonVpnKeys)
        {
            foreach (ContainerSpec spec in _registry.GetRequired(key).Definition.BuildContainerSpecs(configuration, resolver, stackarr))
            {
                await Assert.That(spec.Traefik).IsNotNull();
                await Assert.That(spec.NetworkMode).IsNull();
            }
        }
    }

    [Test]
    public async Task TraefikService_HasDockerSocketAndPorts80And443_WithoutTraefikConfig()
    {
        IConfiguration configuration = Config();
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        ContainerSpec spec = _registry.GetRequired("reverse_proxy")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        await Assert.That(spec.Volumes.Any(v => v.ContainerPath == "/var/run/docker.sock")).IsTrue();
        List<int> hostPorts = spec.Ports.Select(p => p.HostPort).ToList();
        await Assert.That(hostPorts).Contains(80);
        await Assert.That(hostPorts).Contains(443);
        await Assert.That(spec.Traefik).IsNull();
    }

    [Test]
    public async Task GluetunService_HasNetAdminCapabilityAndTunDevice()
    {
        IConfiguration configuration = Config();
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        await Assert.That(spec.Capabilities).Contains("NET_ADMIN");
        await Assert.That(spec.Devices).Contains("/dev/net/tun:/dev/net/tun");
    }

    [Test]
    public async Task GluetunService_MapsVpnConfigToEnvironment()
    {
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(
            vpnEnabled: true,
            vpnProvider: "protonvpn",
            vpnType: "wireguard",
            vpnWireguardKey: "test-key",
            vpnWireguardAddrs: "10.0.0.1",
            vpnServerCountries: "France");
        StackarrConfig config = ConfigDefaults.GetDefaults();
        VolumePathResolver resolver = new(config.Storage);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        await Assert.That(spec.Environment["VPN_SERVICE_PROVIDER"]).IsEqualTo("protonvpn");
        await Assert.That(spec.Environment["VPN_TYPE"]).IsEqualTo("wireguard");
        await Assert.That(spec.Environment["WIREGUARD_PRIVATE_KEY"]).IsEqualTo("test-key");
        await Assert.That(spec.Environment["WIREGUARD_ADDRESSES"]).IsEqualTo("10.0.0.1");
        await Assert.That(spec.Environment["SERVER_COUNTRIES"]).IsEqualTo("France");
    }

    [Test]
    public async Task GluetunService_OpenVpnType_MapsCredentialsToOpenVpnEnv()
    {
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(
            vpnEnabled: true,
            vpnType: "openvpn",
            vpnWireguardKey: "user",
            vpnWireguardAddrs: "pass");
        StackarrConfig config = ConfigDefaults.GetDefaults();
        VolumePathResolver resolver = new(config.Storage);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        await Assert.That(spec.Environment["OPENVPN_USER"]).IsEqualTo("user");
        await Assert.That(spec.Environment["OPENVPN_PASSWORD"]).IsEqualTo("pass");
    }

    [Test]
    public async Task GluetunService_TorrentRouting_DoesNotPublishPeerPort()
    {
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        // Outbound-only by design: a host bind would be unreachable behind NAT/tunnels and
        // would collide with another stack on the same host.
        await Assert.That(spec.Ports.Any(p => p.ContainerPort == 51413)).IsFalse();
    }

    [Test]
    public async Task GluetunService_PortForwardingAndExtraPorts_MapsEnvAndPorts()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["vpn:enabled"] = "true",
                ["vpn:portForwarding"] = "true",
                ["vpn:firewallOutboundSubnets"] = "172.18.0.0/16",
                ["vpn:ports:0:host"] = "8080",
                ["vpn:ports:0:container"] = "8080",
                ["vpn:ports:0:protocol"] = "tcp"
            })
            .Build();
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        await Assert.That(spec.Environment["VPN_PORT_FORWARDING"]).IsEqualTo("on");
        await Assert.That(spec.Environment["FIREWALL_OUTBOUND_SUBNETS"]).IsEqualTo("172.18.0.0/16");
        await Assert.That(spec.Ports.Any(p => p.HostPort == 8080 && p.ContainerPort == 8080)).IsTrue();
    }

    [Test]
    public async Task GluetunService_DisabledWhenNeitherMasterNorOptIn()
    {
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: false, torrentVpn: false);
        await Assert.That(new GluetunServiceDefinition().IsEnabled(configuration)).IsFalse();
    }

    [Test]
    public async Task GluetunService_TorrentRouting_SelectsOnlyP2PServers()
    {
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        // A random server pick can land on a non-P2P server where peers are blocked, which
        // presents as torrents sitting at 0% despite a healthy tunnel.
        await Assert.That(spec.Environment["PORT_FORWARD_ONLY"]).IsEqualTo("on");
    }

    [Test]
    public async Task GluetunService_NonTorrentRouting_DoesNotRestrictToP2P()
    {
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: false);
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        await Assert.That(spec.Environment.ContainsKey("PORT_FORWARD_ONLY")).IsFalse();
    }

    [Test]
    public async Task GluetunService_ProviderWithoutPortForwardFilter_DoesNotSetIt()
    {
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(
            vpnEnabled: true, torrentVpn: true, vpnProvider: "mullvad");
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        await Assert.That(spec.Environment.ContainsKey("PORT_FORWARD_ONLY")).IsFalse();
    }

    [Test]
    public async Task GluetunService_EnablesHttpProxyForVpnSpeedtest()
    {
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        await Assert.That(spec.Environment["HTTPPROXY"]).IsEqualTo("on");
        await Assert.That(spec.Environment["HTTPPROXY_PORT"]).IsEqualTo(GluetunServiceDefinition.HttpProxyPort.ToString());
    }

    [Test]
    public async Task GluetunService_EnablesControlServerForTunnelRestart()
    {
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
        (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context();

        ContainerSpec spec = _registry.GetRequired("vpn")
            .Definition.BuildContainerSpecs(configuration, resolver, stackarr).First();

        await Assert.That(spec.Environment["HTTP_CONTROL_SERVER_ADDRESS"])
            .IsEqualTo($":{GluetunServiceDefinition.ControlServerPort}");
        await Assert.That(spec.Environment["HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE"])
            .IsEqualTo($"{{\"auth\":\"apikey\",\"apikey\":\"{GluetunModule.ControlApiKey}\"}}");

        // The control server stays on the internal Docker network, never published to the host.
        await Assert.That(spec.Ports.Any(p => p.ContainerPort == GluetunServiceDefinition.ControlServerPort)).IsFalse();
    }

}
