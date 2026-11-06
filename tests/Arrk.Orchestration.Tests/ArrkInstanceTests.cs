using Microsoft.Extensions.Configuration;
using Arrk.Core;

namespace Arrk.Orchestration.Tests;

public class ArrkInstanceTests
{
    static IConfiguration Config(string? stackName = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StackName"] = stackName
            })
            .Build();

    [Test]
    public async Task ExplicitStackName_IsUsedForNamesAndPrefix()
    {
        ArrkInstance instance = new(Config("demo"));

        await Assert.That(instance.StackName).IsEqualTo("demo");
        await Assert.That(instance.ContainerPrefix).IsEqualTo("stackarr-demo-");
        await Assert.That(instance.GetContainerName("radarr")).IsEqualTo("stackarr-demo-radarr");
        await Assert.That(instance.NetworkName).IsEqualTo("stackarr-demo-net");
    }

    [Test]
    public async Task NetworkName_IsScopedToStackName_SoInstancesAreIsolated()
    {
        ArrkInstance alpha = new(Config("alpha"));
        ArrkInstance beta = new(Config("beta"));

        await Assert.That(alpha.NetworkName).IsEqualTo("stackarr-alpha-net");
        await Assert.That(beta.NetworkName).IsEqualTo("stackarr-beta-net");
        await Assert.That(alpha.NetworkName).IsNotEqualTo(beta.NetworkName);
    }

    [Test]
    public async Task NetworkNameOverride_IsUsedVerbatim()
    {
        ArrkInstance instance = new(Config("demo"), networkName: "shared-test-net");

        await Assert.That(instance.NetworkName).IsEqualTo("shared-test-net");
    }

    [Test]
    public async Task GeneratedStackName_IsEightDigits_AndWrittenBackToConfig()
    {
        IConfigurationRoot configuration = (IConfigurationRoot)Config(null);

        ArrkInstance instance = new(configuration);

        await Assert.That(instance.StackName).HasLength(8);
        await Assert.That(instance.StackName.All(char.IsDigit)).IsTrue();
        await Assert.That(configuration["StackName"]).IsEqualTo(instance.StackName);
    }

    [Test]
    public async Task GetVpnNetworkMode_UsesPrefixedGluetunServiceName()
    {
        ArrkInstance instance = new(Config("demo"));

        await Assert.That(instance.GetVpnNetworkMode()).IsEqualTo("container:stackarr-demo-gluetun");
    }

    [Test]
    public async Task GetVpnContainerName_UsesPrefixedGluetun()
    {
        ArrkInstance instance = new(Config("demo"));

        await Assert.That(instance.GetVpnContainerName()).IsEqualTo("stackarr-demo-gluetun");
    }

    [Test]
    public async Task ResolveServiceHost_ReturnsGluetunWhenVpnRouted_ElseServiceName()
    {
        ArrkInstance instance = new(Config("demo"));

        await Assert.That(instance.ResolveServiceHost("stackarr-demo-transmission", vpnRouted: false))
            .IsEqualTo("stackarr-demo-transmission");
        await Assert.That(instance.ResolveServiceHost("stackarr-demo-transmission", vpnRouted: true))
            .IsEqualTo("stackarr-demo-gluetun");
    }
}
