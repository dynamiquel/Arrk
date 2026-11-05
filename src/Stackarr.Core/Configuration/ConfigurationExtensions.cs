using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stackarr.Core.Configuration;

public static class ConfigurationExtensions
{
    /// <summary>
    /// Binds <typeparamref name="T"/> from <paramref name="configuration"/> with replace semantics
    /// for lists (see <see cref="ConfigOverlay"/>), and keeps it hot-reloadable.
    /// Use this instead of <c>services.Configure&lt;T&gt;(section)</c> for config POCOs that have
    /// non-empty collection defaults.
    /// </summary>
    public static IServiceCollection ConfigureConfig<T>(this IServiceCollection services, IConfiguration configuration)
        where T : class
    {
        // The caller passes the section to bind (or the root). Capture it directly instead of
        // resolving IConfiguration from DI, so the helper works in hosts/tests without it.
        services.AddOptions<T>().Configure(options => ConfigOverlay.Apply(configuration, options));

        services.AddSingleton<IOptionsChangeTokenSource<T>>(
            new ConfigurationChangeTokenSource<T>(configuration));

        return services;
    }

    /// <summary>
    /// Options-builder form of <see cref="ConfigureConfig{T}(IServiceCollection, IConfiguration)"/>,
    /// so validation can be chained.
    /// </summary>
    public static OptionsBuilder<T> ConfigureConfig<T>(this OptionsBuilder<T> builder, IConfiguration configuration)
        where T : class
    {
        builder.Services.ConfigureConfig<T>(configuration);
        return builder;
    }

    /// <summary>
    /// Checks if a service section is enabled. Returns true by default when the
    /// config section is absent (services are enabled unless explicitly disabled).
    /// </summary>
    public static bool IsServiceEnabled(this IConfiguration configuration, string sectionPath)
    {
        return configuration.GetValue<bool?>($"{sectionPath}:enabled") ?? true;
    }

    /// <summary>
    /// Checks whether a service should be exposed through the reverse proxy. Returns the
    /// supplied <paramref name="defaultValue"/> when the section/flag is absent so callers
    /// decide which services are proxied out of the box (media players on, the rest off).
    /// </summary>
    public static bool IsServiceProxied(this IConfiguration configuration, string sectionPath, bool defaultValue)
    {
        return configuration.GetValue<bool?>($"{sectionPath}:proxy") ?? defaultValue;
    }

    /// <summary>
    /// Whether a service has opted into routing its outbound traffic through the VPN
    /// (Gluetun) container via its own <c>vpn</c> flag. This is only half the decision;
    /// see <see cref="IsTorrentVpnRouted"/> for the effective routing state.
    /// </summary>
    public static bool IsServiceVpnOptIn(this IConfiguration configuration, string sectionPath)
    {
        return configuration.GetValue<bool?>($"{sectionPath}:vpn") ?? false;
    }

    /// <summary>
    /// Effective VPN routing for the torrent downloader: it opts in via
    /// <c>services.torrentDownloader.vpn</c>. Opting in is sufficient to provision the tunnel
    /// even when <c>vpn.enabled</c> is not set, so a user can enable it in one place.
    /// </summary>
    public static bool IsTorrentVpnRouted(this IConfiguration configuration)
    {
        return configuration.IsServiceVpnOptIn("services:torrentDownloader");
    }

    /// <summary>
    /// Effective VPN routing for the Soulseek source (slskd): it opts in via <c>services.slskd.vpn</c>.
    /// A different path/IP often fixes an unreliable direct connection to Soulseek.
    /// </summary>
    public static bool IsSlskdVpnRouted(this IConfiguration configuration)
    {
        return configuration.IsServiceVpnOptIn("services:slskd");
    }

    /// <summary>
    /// Whether the VPN (Gluetun) container must be provisioned at all, either explicitly
    /// enabled, or required by a service that has opted in.
    /// </summary>
    public static bool IsVpnTunnelRequired(this IConfiguration configuration)
    {
        return configuration.GetValue<bool?>("vpn:enabled") == true
            || configuration.IsServiceVpnOptIn("services:torrentDownloader")
            || configuration.IsServiceVpnOptIn("services:slskd");
    }
}
