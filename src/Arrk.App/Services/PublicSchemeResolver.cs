using Arrk.Core.Configuration;

namespace Arrk.App.Services;

/// <summary>
/// Decides whether an incoming request that reached Arrk over plain HTTP is actually the
/// public HTTPS origin (delivered through Traefik / the Cloudflare tunnel, which may not pass
/// trusted forwarded headers). The decision uses the <em>current</em> <c>routes</c> configuration
/// so changing <c>routes.public_url</c> live takes effect on the next request instead of after a
/// restart.
/// </summary>
public static class PublicSchemeResolver
{
    /// <summary>
    /// True when the request should be treated as HTTPS because its host matches the configured
    /// public URL. <paramref name="forwardedHost"/> (the <c>X-Forwarded-Host</c> header) takes
    /// precedence over the request host, matching how the proxy sees the origin.
    /// </summary>
    public static bool ShouldForceHttps(bool isHttps, string? forwardedHost, string requestHost, RoutesConfig routes)
        => ShouldForceHttps(isHttps, forwardedHost, requestHost, routes.ResolvePublicBaseUrl());

    public static bool ShouldForceHttps(bool isHttps, string? forwardedHost, string requestHost, string publicBaseUrl)
    {
        if (isHttps)
            return false;

        if (string.IsNullOrWhiteSpace(publicBaseUrl)
            || !publicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out Uri? publicUri))
            return false;

        string host = !string.IsNullOrWhiteSpace(forwardedHost) ? forwardedHost : requestHost;
        if (string.IsNullOrWhiteSpace(host))
            return false;

        // The forwarded host may carry an explicit default port (e.g. ":443"); normalise both
        // sides so "media.example.com" and "media.example.com:443" are treated as the same origin.
        string publicAuthority = publicUri.IsDefaultPort ? publicUri.Host : $"{publicUri.Host}:{publicUri.Port}";
        return string.Equals(NormalizeAuthority(host, 443), publicAuthority, StringComparison.OrdinalIgnoreCase);
    }

    static string NormalizeAuthority(string authority, int defaultPort)
    {
        string value = authority.Trim();
        int colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out int port))
        {
            string name = value[..colon];
            return port == defaultPort ? name : $"{name}:{port}";
        }

        return value;
    }
}
