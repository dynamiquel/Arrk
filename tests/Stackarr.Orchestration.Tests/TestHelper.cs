using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.App;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;

namespace Stackarr.Orchestration.Tests;

public static class TestHelper
{
    public static ServiceRegistry CreateDefault()
    {
        return ServiceRegistryFactory.CreateDefault();
    }

    public static ModuleLoader CreateModuleLoader()
    {
        return new ModuleLoader(NullLogger<ModuleLoader>.Instance);
    }

    public static ServiceRegistry CreateFromModules(params IStackarrModule[] modules)
    {
        ModuleLoader loader = CreateModuleLoader();
        foreach (IStackarrModule module in modules)
            loader.RegisterModule(module);
        return loader.BuildRegistry();
    }

    /// <summary>
    /// Builds an in-memory <see cref="IConfiguration"/> for the standard service toggles
    /// shared by many orchestration tests.
    /// </summary>
    public static IConfiguration CreateServiceConfiguration(
        bool tvEnabled = true,
        bool musicEnabled = true,
        bool jellyfinEnabled = true,
        bool navidromeEnabled = true,
        bool vpnEnabled = false,
        bool jellyfinNvidia = false,
        string vpnProvider = "protonvpn",
        string vpnType = "wireguard",
        string vpnWireguardKey = "",
        string vpnWireguardAddrs = "",
        string vpnServerCountries = "",
        bool torrentVpn = false,
        bool? proxyAll = true)
    {
        Dictionary<string, string?> values = new()
        {
            ["services:tv:enabled"] = tvEnabled.ToString().ToLowerInvariant(),
            ["services:music:enabled"] = musicEnabled.ToString().ToLowerInvariant(),
            ["services:slskd:enabled"] = musicEnabled.ToString().ToLowerInvariant(),
            ["services:jellyfin:enabled"] = jellyfinEnabled.ToString().ToLowerInvariant(),
            ["services:jellyfin:nvidia"] = jellyfinNvidia.ToString().ToLowerInvariant(),
            ["services:navidrome:enabled"] = navidromeEnabled.ToString().ToLowerInvariant(),
            ["vpn:enabled"] = vpnEnabled.ToString().ToLowerInvariant(),
            ["services:torrentDownloader:vpn"] = torrentVpn.ToString().ToLowerInvariant(),
            ["vpn:provider"] = vpnProvider,
            ["vpn:type"] = vpnType,
            ["vpn:wireguardPrivateKey"] = vpnWireguardKey,
            ["vpn:wireguardAddresses"] = vpnWireguardAddrs,
            ["vpn:serverCountries"] = vpnServerCountries
        };

        // Tests generally assert the proxied spec. Unless a test opts out, enable the proxy
        // flag for every service so Traefik labels/routes are generated as before.
        if (proxyAll is not null)
        {
            string value = proxyAll.Value.ToString().ToLowerInvariant();
            foreach (string section in ProxySections)
                values[$"{section}:proxy"] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    static readonly string[] ProxySections =
    [
        "services:tv", "services:music", "services:jellyfin",
        "services:navidrome", "services:slskd", "services:torrentDownloader", "services:subtitles:bazarr", "indexers"
    ];

    public static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
}
