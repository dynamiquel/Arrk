using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Slskd;

/// <summary>
/// Stackarr-provisioned slskd container. Joins Stackarr's own network and mounts Stackarr's
/// downloads root only, so it is fully isolated from any other client on the host.
/// </summary>
public class SlskdServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "slskd";

    public string Key => StaticKey;
    public string DisplayName => "Soulseek";
    public string Description => "Soulseek (slskd) track-level source";
    public bool IsInfrastructure => false;
    public IReadOnlyList<string> ProvisionBefore => [];
    public IReadOnlyList<string> ProvisionAfter => ["torrent_downloader", "vpn"];

    public bool IsEnabled(IConfiguration configuration) => configuration.IsServiceEnabled("services:slskd");

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(
        IConfiguration configuration, VolumePathResolver volumeResolver, StackarrInstance stackarr)
    {
        SlskdConfig cfg = configuration.GetSection("services:slskd").Get<SlskdConfig>() ?? new();
        bool proxy = configuration.IsServiceProxied("services:slskd", defaultValue: false);
        bool vpnRouted = configuration.IsSlskdVpnRouted();
        string? networkMode = vpnRouted ? stackarr.GetVpnNetworkMode() : null;

        Dictionary<string, string> env = new(StackarrServiceExtensions.GetDefaultEnvironment())
        {
            ["SLSKD_UMASK"] = "0022",
            ["SLSKD_HTTP_PORT"] = "5030",
            ["SLSKD_SLSK_USERNAME"] = cfg.SoulseekUsername,
            ["SLSKD_SLSK_PASSWORD"] = cfg.SoulseekPassword,
            ["SLSKD_DOWNLOADS_DIR"] = "/downloads/slskd",
            ["SLSKD_INCOMPLETE_DIR"] = "/downloads/slskd/incomplete",
            ["SLSKD_SHARED_DIR"] = "/downloads/slskd/shares",
            // Allow the runtime options overlay (speed limits applied by SlskdLimitsJob).
            ["SLSKD_REMOTE_CONFIGURATION"] = "true"
        };

        // Max concurrent transfers (slots) + the global speed caps (MB/s → KiB/s). Slots only take
        // effect at container start, which is why they live here rather than in the options overlay.
        TorrentDownloaderConfig torrents = configuration.GetSection("services:torrentDownloader").Get<TorrentDownloaderConfig>() ?? new();
        env["SLSKD_DOWNLOAD_SLOTS"] = Math.Max(1, cfg.DownloadSlots).ToString();
        env["SLSKD_UPLOAD_SLOTS"] = Math.Max(1, cfg.UploadSlots).ToString();
        if (torrents.Speed.DownloadMbps > 0)
            env["SLSKD_DOWNLOAD_SPEED_LIMIT"] = (torrents.Speed.DownloadMbps * 1024).ToString();
        if (torrents.Speed.UploadMbps > 0)
            env["SLSKD_UPLOAD_SPEED_LIMIT"] = (torrents.Speed.UploadMbps * 1024).ToString();

        return
        [
            new()
            {
                Image = "slskd/slskd:latest",
                Name = stackarr.GetContainerName("slskd"),
                Environment = env,
                Volumes =
                [
                    new()
                    {
                        HostPath = $"{stackarr.ContainerPrefix}slskd-config",
                        ContainerPath = "/app",
                        Type = VolumeType.NamedVolume,
                        Permissions = VolumePerms.ReadWrite
                    },
                    new()
                    {
                        HostPath = RelativeVolumes.Downloads,
                        ContainerPath = "/downloads",
                        Type = VolumeType.Bind,
                        Permissions = VolumePerms.ReadWrite
                    }
                ],
                Labels = new()
                {
                    [ContainerLabels.Service] = StaticKey
                },
                // slskd refuses to start unless its download directories already exist, and they
                // are subdirectories of the mounted /downloads root (not mount points themselves).
                RequiredDirectories =
                [
                    "$downloads/slskd",
                    "$downloads/slskd/incomplete",
                    "$downloads/slskd/shares"
                ],
                NetworkName = stackarr.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped,
                NetworkMode = networkMode,
                Traefik = proxy ? new()
                {
                    ServiceName = "slskd",
                    Rule = "PathPrefix(`/slskd`)",
                    Port = 5030
                } : null
            }
        ];
    }
}
