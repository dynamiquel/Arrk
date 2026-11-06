using Microsoft.Extensions.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;

namespace Arrk.Cloudflared.Tests;

public class CloudflaredServiceDefinitionTests
{
    const string Token = "test-tunnel-token";

    static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build();

    static IConfiguration TokenConfig(string token = Token, bool? enabled = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["routes:cloudflared:tunnelToken"] = token
        };
        if (enabled.HasValue)
            values["routes:cloudflared:enabled"] = enabled.Value.ToString().ToLowerInvariant();
        return Config(values);
    }

    static ArrkInstance Arrk()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "cloudflared-tests" })
            .Build();
        return new ArrkInstance(configuration);
    }

    static (VolumePathResolver Resolver, ArrkInstance Arrk) Context()
    {
        ArrkConfig config = new();
        return (new VolumePathResolver(config.Storage), Arrk());
    }

    static ContainerSpec Spec(IConfiguration configuration)
    {
        (VolumePathResolver resolver, ArrkInstance arrk) = Context();
        return new CloudflaredServiceDefinition()
            .BuildContainerSpecs(configuration, resolver, arrk)
            .Single();
    }

    [Test]
    public async Task IsEnabled_False_WhenNoTokenConfigured()
    {
        await Assert.That(new CloudflaredServiceDefinition().IsEnabled(Config())).IsFalse();
    }

    [Test]
    public async Task IsEnabled_True_WhenTokenConfigured_AndEnabledByDefault()
    {
        await Assert.That(new CloudflaredServiceDefinition().IsEnabled(TokenConfig())).IsTrue();
    }

    [Test]
    public async Task IsEnabled_False_WhenTokenConfigured_ButExplicitlyDisabled()
    {
        await Assert.That(new CloudflaredServiceDefinition().IsEnabled(TokenConfig(enabled: false))).IsFalse();
    }

    [Test]
    public async Task IsEnabled_False_WhenTokenIsWhitespace()
    {
        await Assert.That(new CloudflaredServiceDefinition().IsEnabled(TokenConfig("   "))).IsFalse();
    }

    [Test]
    public async Task BuildContainerSpecs_UsesCloudflaredImage_AndNamespacedContainerName()
    {
        ArrkInstance arrk = Arrk();
        (VolumePathResolver resolver, _) = Context();

        ContainerSpec spec = new CloudflaredServiceDefinition()
            .BuildContainerSpecs(TokenConfig(), resolver, arrk).Single();

        await Assert.That(spec.Image).IsEqualTo(CloudflaredServiceDefinition.Image);
        await Assert.That(spec.Image).IsEqualTo("cloudflare/cloudflared:latest");
        await Assert.That(spec.Name).IsEqualTo(arrk.GetContainerName("cloudflared"));
    }

    [Test]
    public async Task BuildContainerSpecs_MapsTokenToTunnelTokenEnvironment()
    {
        ContainerSpec spec = Spec(TokenConfig("secret-token"));

        await Assert.That(spec.Environment["TUNNEL_TOKEN"]).IsEqualTo("secret-token");
        await Assert.That(spec.Environment["TZ"]).IsEqualTo("Etc/UTC");
    }

    [Test]
    public async Task BuildContainerSpecs_RunsTunnelWithoutAutoUpdate()
    {
        ContainerSpec spec = Spec(TokenConfig());

        await Assert.That(spec.Command).Contains("tunnel");
        await Assert.That(spec.Command).Contains("run");
        await Assert.That(spec.Command).Contains("--no-autoupdate");
    }

    [Test]
    public async Task BuildContainerSpecs_JoinsArrkNetwork_AndPublishesNoHostPorts()
    {
        ArrkInstance arrk = Arrk();
        (VolumePathResolver resolver, _) = Context();

        ContainerSpec spec = new CloudflaredServiceDefinition()
            .BuildContainerSpecs(TokenConfig(), resolver, arrk).Single();

        await Assert.That(spec.NetworkName).IsEqualTo(arrk.NetworkName);
        await Assert.That(spec.NetworkMode).IsNull();
        await Assert.That(spec.Ports).IsEmpty();
        await Assert.That(spec.Volumes).IsEmpty();
    }

    [Test]
    public async Task BuildContainerSpecs_HasServiceLabel_AndNoTraefikExposure()
    {
        ContainerSpec spec = Spec(TokenConfig());

        await Assert.That(spec.Labels[ContainerLabels.Service]).IsEqualTo(CloudflaredServiceDefinition.StaticKey);
        await Assert.That(spec.Traefik).IsNull();
        await Assert.That(spec.RestartPolicy).IsEqualTo(RestartPolicyType.UnlessStopped);
    }

    [Test]
    public async Task ProvisionAfter_ContainsReverseProxy_SoTraefikExistsFirst()
    {
        IReadOnlyList<string> after = new CloudflaredServiceDefinition().ProvisionAfter;

        await Assert.That(after).Contains("reverse_proxy");
    }
}
