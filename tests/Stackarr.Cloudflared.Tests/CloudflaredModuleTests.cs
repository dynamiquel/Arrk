using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;

namespace Stackarr.Cloudflared.Tests;

public class CloudflaredModuleTests
{
    static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build();


    [Test]
    public async Task GetServices_ReturnsSingleCloudflaredService()
    {
        IReadOnlyList<Service> services = new CloudflaredModule().GetServices();

        await Assert.That(services.Count).IsEqualTo(1);
        await Assert.That(services[0].Definition.Key).IsEqualTo(CloudflaredServiceDefinition.StaticKey);
        await Assert.That(services[0].Definition).IsTypeOf<CloudflaredServiceDefinition>();
    }

    [Test]
    public async Task ConfigureServices_BindsCloudflaredConfigSection()
    {
        ServiceCollection services = new();
        services.AddLogging();
        IConfiguration configuration = Config(new()
        {
            ["routes:cloudflared:tunnelToken"] = "secret"
        });

        new CloudflaredModule().ConfigureServices(services, configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        CloudflaredConfig bound = provider.GetRequiredService<IOptionsMonitor<CloudflaredConfig>>().CurrentValue;

        await Assert.That(bound.TunnelToken).IsEqualTo("secret");
        await Assert.That(bound.Enabled).IsTrue();
    }

    [Test]
    public async Task GetDependencies_DeclaresCloudflared()
    {
        IReadOnlyList<ModuleDependency> dependencies = new CloudflaredModule().GetDependencies();

        await Assert.That(dependencies.Count).IsEqualTo(1);
        await Assert.That(dependencies[0].Name).IsEqualTo("cloudflared");
        await Assert.That(dependencies[0].Url).Contains("cloudflared");
    }
}
