using Arrk.Core.Models;
using Arrk.Core.Services;

namespace Arrk.Orchestration.Tests;

/// <summary>
/// Unit coverage for the spec fingerprint and shared-network detection that make runtime
/// config reconciliation safe and idempotent.
/// </summary>
public class ServiceProvisionerSpecTests
{
    static ContainerSpec BaseSpec() => new()
    {
        Image = "nginx:alpine",
        Name = "stackarr-test-thing",
        NetworkName = "stackarr-net"
    };

    [Test]
    public async Task ComputeSpecHash_IsStableForSameSpec()
    {
        await Assert.That(ServiceProvisioner.ComputeSpecHash(BaseSpec()))
            .IsEqualTo(ServiceProvisioner.ComputeSpecHash(BaseSpec()));
    }

    [Test]
    public async Task ComputeSpecHash_ChangesWithNetworkMode()
    {
        ContainerSpec routed = BaseSpec();
        ContainerSpec plain = BaseSpec();

        routed = new()
        {
            Image = routed.Image,
            Name = routed.Name,
            NetworkName = routed.NetworkName,
            NetworkMode = "service:stackarr-test-gluetun"
        };

        await Assert.That(ServiceProvisioner.ComputeSpecHash(routed))
            .IsNotEqualTo(ServiceProvisioner.ComputeSpecHash(plain));
    }

    [Test]
    public async Task ComputeSpecHash_ChangesWithEnvironmentAndPorts()
    {
        ContainerSpec a = BaseSpec();
        ContainerSpec b = BaseSpec();
        b.Environment["VPN_TYPE"] = "wireguard";
        b.Ports.Add(new PortMapping { HostPort = 51413, ContainerPort = 51413 });

        await Assert.That(ServiceProvisioner.ComputeSpecHash(a))
            .IsNotEqualTo(ServiceProvisioner.ComputeSpecHash(b));
    }

    [Test]
    public async Task ComputeSpecHash_ChangesWithRequiredDirectories()
    {
        ContainerSpec a = BaseSpec();
        ContainerSpec b = BaseSpec();
        b.RequiredDirectories.Add("$downloads/slskd");

        await Assert.That(ServiceProvisioner.ComputeSpecHash(a))
            .IsNotEqualTo(ServiceProvisioner.ComputeSpecHash(b));
    }

    [Test]
    public async Task ComputeSpecHash_IsIndependentOfEnvironmentOrder()
    {
        ContainerSpec a = BaseSpec();
        a.Environment["A"] = "1";
        a.Environment["B"] = "2";

        ContainerSpec b = BaseSpec();
        b.Environment["B"] = "2";
        b.Environment["A"] = "1";

        await Assert.That(ServiceProvisioner.ComputeSpecHash(a))
            .IsEqualTo(ServiceProvisioner.ComputeSpecHash(b));
    }

    [Test]
    public async Task UsesSharedNetwork_TrueForServiceContainerHostAndNone()
    {
        foreach (string mode in new[] { "service:stackarr-test-gluetun", "container:abc123", "host", "none" })
        {
            ContainerSpec spec = new() { Image = "x", Name = "y", NetworkMode = mode };
            await Assert.That(ServiceProvisioner.UsesSharedNetwork(spec)).IsTrue();
        }
    }

    [Test]
    public async Task UsesSharedNetwork_FalseForNullOrDefaultNetwork()
    {
        await Assert.That(ServiceProvisioner.UsesSharedNetwork(BaseSpec())).IsFalse();
        ContainerSpec bridge = new() { Image = "x", Name = "y", NetworkMode = "bridge" };
        await Assert.That(ServiceProvisioner.UsesSharedNetwork(bridge)).IsFalse();
    }

    [Test]
    public async Task ParseDevice_SplitsHostAndContainerPaths()
    {
        (string host, string container) = ServiceProvisioner.ParseDevice("/dev/net/tun:/dev/net/tun");
        await Assert.That(host).IsEqualTo("/dev/net/tun");
        await Assert.That(container).IsEqualTo("/dev/net/tun");

        (string host2, string container2) = ServiceProvisioner.ParseDevice("/dev/dri:/dev/dri");
        await Assert.That(host2).IsEqualTo("/dev/dri");
        await Assert.That(container2).IsEqualTo("/dev/dri");
    }

    [Test]
    public async Task ParseDevice_WithoutContainerPath_DefaultsToHostPath()
    {
        (string host, string container) = ServiceProvisioner.ParseDevice("/dev/net/tun");
        await Assert.That(host).IsEqualTo("/dev/net/tun");
        await Assert.That(container).IsEqualTo("/dev/net/tun");
    }
}
