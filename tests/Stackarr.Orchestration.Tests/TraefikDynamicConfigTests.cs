using Microsoft.Extensions.Configuration;
using Stackarr.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Services;
using Stackarr.Orchestration;

namespace Stackarr.Orchestration.Tests;

public class TraefikDynamicConfigTests
{
    static (StackarrConfig Config, VolumePathResolver Resolver, StackarrInstance Stackarr) Context(string configDir)
    {
        StackarrConfig config = ConfigDefaults.GetDefaults();
        config.Storage.Config = configDir;
        return (config, new VolumePathResolver(config.Storage), OrchestrationTestHelper.CreateStackarrInstance());
    }

    [Test]
    public async Task WriteStackarrUiRoute_WritesFile_WhenProxiedAndContainerKnown()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context(dir);

            TraefikDynamicConfig.WriteStackarrUiRoute(config, resolver, stackarr, "stackarr-self");

            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.StackarrFileName);
            await Assert.That(File.Exists(path)).IsTrue();

            string content = File.ReadAllText(path);
            await Assert.That(content).Contains("stackarr-self:5186");
            await Assert.That(content).Contains("PathPrefix(`/`)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteStackarrUiRoute_NoFile_WhenProxyDisabled()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context(dir);
            config.Routes.Proxy = false;

            TraefikDynamicConfig.WriteStackarrUiRoute(config, resolver, stackarr, "stackarr-self");

            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.StackarrFileName);
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteStackarrUiRoute_RemovesStaleFile_WhenContainerUnknown()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context(dir);
            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.StackarrFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "stale");

            TraefikDynamicConfig.WriteStackarrUiRoute(config, resolver, stackarr, null);

            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteAllRoutes_AppliesProxyToggle_AndKeepsVpnRoutes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context(dir);
            IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
            ServiceRegistry registry = TestHelper.CreateDefault();

            string uiPath = Path.Combine(dir, "traefik", TraefikDynamicConfig.StackarrFileName);
            string vpnPath = Path.Combine(dir, "traefik", TraefikDynamicConfig.VpnFileName);

            TraefikDynamicConfig.WriteAllRoutes(config, resolver, stackarr, registry, configuration, "stackarr-self");
            await Assert.That(File.Exists(uiPath)).IsTrue();
            await Assert.That(File.Exists(vpnPath)).IsTrue();

            // Toggling routes.proxy off must remove the UI route on the live reconcile.
            config.Routes.Proxy = false;
            TraefikDynamicConfig.WriteAllRoutes(config, resolver, stackarr, registry, configuration, "stackarr-self");

            await Assert.That(File.Exists(uiPath)).IsFalse();
            await Assert.That(File.Exists(vpnPath)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteVpnRoutes_WhenTorrentRouted_WritesRoutePointingAtGluetun()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context(dir);
            IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
            ServiceRegistry registry = TestHelper.CreateDefault();

            TraefikDynamicConfig.WriteVpnRoutes(config, resolver, stackarr, registry, configuration);

            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.VpnFileName);
            await Assert.That(File.Exists(path)).IsTrue();

            string content = File.ReadAllText(path);
            await Assert.That(content).Contains("transmission:");
            await Assert.That(content).Contains("PathPrefix(`/transmission`)");
            await Assert.That(content).Contains($"http://{stackarr.GetVpnContainerName()}:9091");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteVpnRoutes_WhenNotRouted_RemovesStaleFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (StackarrConfig config, VolumePathResolver resolver, StackarrInstance stackarr) = Context(dir);
            IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: false);
            ServiceRegistry registry = TestHelper.CreateDefault();

            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.VpnFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "stale");

            TraefikDynamicConfig.WriteVpnRoutes(config, resolver, stackarr, registry, configuration);

            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
