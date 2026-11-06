using Microsoft.Extensions.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;

namespace Arrk.Orchestration.Tests;

/// <summary>
/// Covers the reverse proxy host-port configuration added so instances can share a host:
/// setting a port to 0 leaves it unpublished (tunnel-only), and non-standard ports can be
/// chosen to avoid clashing with another instance.
/// </summary>
public class ReverseProxyConfigTests
{
    static ArrkInstance Arrk() => OrchestrationTestHelper.CreateArrkInstance();

    static VolumePathResolver Resolver() => new(new StorageConfig());

    static ContainerSpec Spec(params (string Key, string? Value)[] values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

        return new TraefikServiceDefinition()
            .BuildContainerSpecs(configuration, Resolver(), Arrk()).Single();
    }

    [Test]
    public async Task Default_PublishesHttpAndHttps()
    {
        ContainerSpec spec = Spec();

        await Assert.That(spec.Ports).HasCount(2);
        await Assert.That(spec.Ports.Any(p => p.HostPort == 80 && p.ContainerPort == 80)).IsTrue();
        await Assert.That(spec.Ports.Any(p => p.HostPort == 443 && p.ContainerPort == 443)).IsTrue();
    }

    [Test]
    public async Task ZeroPorts_PublishNothing_ForTunnelOnly()
    {
        ContainerSpec spec = Spec(("routes:httpPort", "0"), ("routes:httpsPort", "0"));

        await Assert.That(spec.Ports).IsEmpty();
    }

    [Test]
    public async Task DisablingHttp_KeepsHttpsPublished()
    {
        ContainerSpec spec = Spec(("routes:httpPort", "0"));

        await Assert.That(spec.Ports).HasCount(1);
        await Assert.That(spec.Ports[0].HostPort).IsEqualTo(443);
        await Assert.That(spec.Ports[0].ContainerPort).IsEqualTo(443);
    }

    [Test]
    public async Task CustomPorts_ArePublishedAgainstContainerEntrypoints()
    {
        ContainerSpec spec = Spec(("routes:httpPort", "8080"), ("routes:httpsPort", "8443"));

        await Assert.That(spec.Ports.Any(p => p.HostPort == 8080 && p.ContainerPort == 80)).IsTrue();
        await Assert.That(spec.Ports.Any(p => p.HostPort == 8443 && p.ContainerPort == 443)).IsTrue();
    }

    [Test]
    public async Task Proxy_AlwaysJoinsPerStackNetwork()
    {
        ArrkInstance arrk = Arrk();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        ContainerSpec spec = new TraefikServiceDefinition()
            .BuildContainerSpecs(configuration, Resolver(), arrk).Single();

        await Assert.That(spec.NetworkName).IsEqualTo(arrk.NetworkName);
        await Assert.That(spec.NetworkName).StartsWith("stackarr-");
        await Assert.That(spec.NetworkName).EndsWith("-net");
    }

    [Test]
    public async Task Proxy_OnlyDiscoversContainersFromItsOwnStack()
    {
        ArrkInstance arrk = Arrk();

        ContainerSpec spec = new TraefikServiceDefinition()
            .BuildContainerSpecs(TestHelper.CreateServiceConfiguration(), Resolver(), arrk).Single();

        // Router/service names are fixed per service, so without a constraint every Traefik on
        // the host would load-balance identical services from other stacks into the same pool.
        await Assert.That(spec.Command).Contains(
            $"--providers.docker.constraints=Label(`stackarr.instance-id`,`{arrk.StackName}`)");
    }
}
