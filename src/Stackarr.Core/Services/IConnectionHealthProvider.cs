namespace Stackarr.Core.Services;

/// <summary>
/// A summary of the network path a subsystem depends on, derived from the most recent successful
/// speed test. It lets a consumer tell "the torrent is bad" apart from "our own connection is
/// bad" before acting (e.g. before striking a stalled torrent).
/// </summary>
public sealed record ConnectionHealth
{
    /// <summary>
    /// Whether the connection is good enough to hold a download responsible for being slow or
    /// stalled. A connection with no usable measurement is reported as healthy (<see langword="true"/>)
    /// so a missing speed test never causes torrents to be kept around (or removed) on bad data.
    /// </summary>
    public required bool IsHealthy { get; init; }

    /// <summary>Whether a successful measurement backed this verdict.</summary>
    public required bool IsKnown { get; init; }

    /// <summary>Measured download speed in Mbps, when known.</summary>
    public double? DownloadMbps { get; init; }

    /// <summary>When the backing speed test ran, when known.</summary>
    public DateTime? TestedAt { get; init; }

    /// <summary>The speed test target this verdict came from ("network" or "vpn").</summary>
    public string Target { get; init; } = "network";

    /// <summary>No recent usable measurement: treat the connection as healthy.</summary>
    public static ConnectionHealth Unknown(string target) =>
        new() { IsHealthy = true, IsKnown = false, Target = target };
}

/// <summary>
/// Reports whether the connection used to download torrents is currently healthy. Implemented by
/// the speedtest module over its stored results; the download manager consumes it so a bad home
/// connection never causes torrents to be removed for being slow.
/// </summary>
public interface IConnectionHealthProvider
{
    /// <summary>
    /// Health of the connection torrent traffic uses, the VPN result when the downloader is
    /// VPN-routed, otherwise the host/network result.
    /// </summary>
    Task<ConnectionHealth> GetDownloadConnectionHealthAsync(CancellationToken ct = default);
}
