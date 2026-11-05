using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Stackarr.Core.Configuration;

public class RoutesConfig
{
    /// <summary>Configuration section this class is bound from.</summary>
    public const string SectionName = "routes";

    [Description("Base URL for reverse proxy routing")]
    public string BaseUrl { get; set; } = "http://localhost";

    [Description("Public URL external clients use to reach Stackarr and its services (e.g. https://media.example.com). Used to build deep links such as Jellyfin. Falls back to routes.base_url.")]
    public string PublicUrl { get; set; } = "";

    [Description("Expose the Stackarr web interface through the reverse proxy at the root path")]
    public bool Proxy { get; set; } = true;

    [Description("Host port the reverse proxy publishes its HTTP entrypoint on. Set to 0 to not publish it (e.g. a tunnel-only deployment).")]
    [Range(0, 65535)]
    public int HttpPort { get; set; } = 80;

    [Description("Host port the reverse proxy publishes its HTTPS entrypoint on. Set to 0 to not publish it (e.g. a tunnel-only deployment).")]
    [Range(0, 65535)]
    public int HttpsPort { get; set; } = 443;

    [Description("Cloudflare Tunnel configuration")]
    public CloudflaredConfig Cloudflared { get; set; } = new();

    [Description("Custom route overrides as path-to-service mapping")]
    public Dictionary<string, string> CustomRoutes { get; set; } = new();

    /// <summary>
    /// The base URL external clients should use to reach Stackarr and its services. Prefers the
    /// explicit <see cref="PublicUrl"/>, then falls back to <see cref="BaseUrl"/>. Used to build
    /// deep links that must be reachable from a browser.
    /// </summary>
    public string ResolvePublicBaseUrl()
    {
        string configured = !string.IsNullOrWhiteSpace(PublicUrl) ? PublicUrl : BaseUrl;

        return string.IsNullOrWhiteSpace(configured) ? "http://localhost" : configured.TrimEnd('/');
    }
}

public class CloudflaredConfig
{
    /// <summary>Configuration path this section is bound from.</summary>
    public const string SectionPath = "routes:cloudflared";

    [Description("Enable the Cloudflare tunnel for remote access")]
    public bool Enabled { get; set; } = true;

    [Description("Cloudflare Tunnel token for remote access")]
    [DataType(DataType.Password)]
    public string TunnelToken { get; set; } = "";
}
