using Microsoft.Extensions.Configuration;
using Arrk.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Services;
using Arrk.Orchestration;

namespace Arrk.Orchestration.Tests;

public class TraefikDynamicConfigTests
{
    static (ArrkConfig Config, VolumePathResolver Resolver, ArrkInstance Arrk) Context(string configDir)
    {
        ArrkConfig config = ConfigDefaults.GetDefaults();
        config.Storage.Config = configDir;
        return (config, new VolumePathResolver(config.Storage), OrchestrationTestHelper.CreateArrkInstance());
    }

    [Test]
    public async Task WriteArrkUiRoute_WritesFile_WhenProxiedAndContainerKnown()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context(dir);

            TraefikDynamicConfig.WriteArrkUiRoute(config, resolver, arrk, "stackarr-self");

            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.ArrkFileName);
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
    public async Task WriteArrkUiRoute_NoFile_WhenProxyDisabled()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context(dir);
            config.Routes.Proxy = false;

            TraefikDynamicConfig.WriteArrkUiRoute(config, resolver, arrk, "stackarr-self");

            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.ArrkFileName);
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WriteArrkUiRoute_RemovesStaleFile_WhenContainerUnknown()
    {
        string dir = Path.Combine(Path.GetTempPath(), "stackarr-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context(dir);
            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.ArrkFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "stale");

            TraefikDynamicConfig.WriteArrkUiRoute(config, resolver, arrk, null);

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
            (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context(dir);
            IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
            ServiceRegistry registry = TestHelper.CreateDefault();

            string uiPath = Path.Combine(dir, "traefik", TraefikDynamicConfig.ArrkFileName);
            string vpnPath = Path.Combine(dir, "traefik", TraefikDynamicConfig.VpnFileName);

            TraefikDynamicConfig.WriteAllRoutes(config, resolver, arrk, registry, configuration, "stackarr-self");
            await Assert.That(File.Exists(uiPath)).IsTrue();
            await Assert.That(File.Exists(vpnPath)).IsTrue();

            // Toggling routes.proxy off must remove the UI route on the live reconcile.
            config.Routes.Proxy = false;
            TraefikDynamicConfig.WriteAllRoutes(config, resolver, arrk, registry, configuration, "stackarr-self");

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
            (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context(dir);
            IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);
            ServiceRegistry registry = TestHelper.CreateDefault();

            TraefikDynamicConfig.WriteVpnRoutes(config, resolver, arrk, registry, configuration);

            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.VpnFileName);
            await Assert.That(File.Exists(path)).IsTrue();

            string content = File.ReadAllText(path);
            await Assert.That(content).Contains("transmission:");
            await Assert.That(content).Contains("PathPrefix(`/transmission`)");
            await Assert.That(content).Contains($"http://{arrk.GetVpnContainerName()}:9091");
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
            (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context(dir);
            IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: false);
            ServiceRegistry registry = TestHelper.CreateDefault();

            string path = Path.Combine(dir, "traefik", TraefikDynamicConfig.VpnFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "stale");

            TraefikDynamicConfig.WriteVpnRoutes(config, resolver, arrk, registry, configuration);

            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
