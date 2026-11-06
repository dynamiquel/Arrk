using System.ComponentModel;

namespace Arrk.Prowlarr.Configuration;

/// <summary>
/// Cloudflare/DDoS challenge solving for indexers, Arrk's "solver". Arrk runs a Byparr
/// container (a FlareSolverr-compatible solver) as a second container of the indexer service and
/// registers it in Prowlarr as a FlareSolverr indexer proxy. Indexers that carry the solver tag
/// are routed through it, so Cloudflare-protected trackers such as 1337x and RuTracker work.
/// </summary>
public class SolverConfig
{
    /// <summary>The tag an indexer must carry for Prowlarr to route it through the solver.</summary>
    public const string DefaultTag = "cf";

    /// <summary>Container port Byparr's FlareSolverr-compatible API listens on.</summary>
    public const int Port = 8191;

    /// <summary>Default image used when none is configured.</summary>
    public const string DefaultImage = "ghcr.io/thephaseless/byparr:latest";

    /// <summary>Default per-challenge time budget, in seconds.</summary>
    public const int DefaultRequestTimeoutSeconds = 180;

    [Description("Run the Byparr solver container so Prowlarr can get past Cloudflare/DDoS challenges on protected indexers")]
    public bool Enabled { get; set; }

    [Description("Container image for the solver")]
    public string Image { get; set; } = DefaultImage;

    [Description("Prowlarr tag that links an indexer to the solver (indexers tagged with this value are routed through it)")]
    public string Tag { get; set; } = DefaultTag;

    [Description("How long the solver may spend on a single challenge, in seconds")]
    public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;

    [Description("Host URL Prowlarr uses to reach the solver. Leave empty to auto-detect the Arrk-managed container.")]
    public string Host { get; set; } = "";

    /// <summary>
    /// The URL Prowlarr should use to reach the solver. Uses the explicit <see cref="Host"/>
    /// override when set, otherwise the Arrk-managed container address on the internal network.
    /// </summary>
    public string ResolveHost(string defaultContainerUrl) =>
        string.IsNullOrWhiteSpace(Host) ? defaultContainerUrl : Host.Trim();

    /// <summary>The effective tag, falling back to <see cref="DefaultTag"/> when blank.</summary>
    public string ResolveTag() => string.IsNullOrWhiteSpace(Tag) ? DefaultTag : Tag.Trim();

    /// <summary>The effective image, falling back to <see cref="DefaultImage"/> when blank.</summary>
    public string ResolveImage() => string.IsNullOrWhiteSpace(Image) ? DefaultImage : Image;

    /// <summary>The effective timeout, falling back to <see cref="DefaultRequestTimeoutSeconds"/> when non-positive.</summary>
    public int ResolveRequestTimeoutSeconds() =>
        RequestTimeoutSeconds > 0 ? RequestTimeoutSeconds : DefaultRequestTimeoutSeconds;
}
