using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Cloudflared.Tests;

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

    static StackarrInstance Stackarr()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "cloudflared-tests" })
            .Build();
        return new StackarrInstance(configuration);
    }

    static (VolumePathResolver Resolver, StackarrInstance Stackarr) Context()
    {
        StackarrConfig config = new();
        return (new VolumePathResolver(config.Storage), Stackarr());
    }

    static ContainerSpec Spec(IConfiguration configuration)
    {
        (VolumePathResolver resolver, StackarrInstance stackarr) = Context();
        return new CloudflaredServiceDefinition()
            .BuildContainerSpecs(configuration, resolver, stackarr)
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
        StackarrInstance stackarr = Stackarr();
        (VolumePathResolver resolver, _) = Context();

        ContainerSpec spec = new CloudflaredServiceDefinition()
            .BuildContainerSpecs(TokenConfig(), resolver, stackarr).Single();

        await Assert.That(spec.Image).IsEqualTo(CloudflaredServiceDefinition.Image);
        await Assert.That(spec.Image).IsEqualTo("cloudflare/cloudflared:latest");
        await Assert.That(spec.Name).IsEqualTo(stackarr.GetContainerName("cloudflared"));
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
    public async Task BuildContainerSpecs_JoinsStackarrNetwork_AndPublishesNoHostPorts()
    {
        StackarrInstance stackarr = Stackarr();
        (VolumePathResolver resolver, _) = Context();

        ContainerSpec spec = new CloudflaredServiceDefinition()
            .BuildContainerSpecs(TokenConfig(), resolver, stackarr).Single();

        await Assert.That(spec.NetworkName).IsEqualTo(stackarr.NetworkName);
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
