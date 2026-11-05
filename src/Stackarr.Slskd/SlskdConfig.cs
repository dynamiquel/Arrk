using System.ComponentModel;

namespace Stackarr.Slskd;

/// <summary>
/// Soulseek (slskd) settings. Stackarr provisions and owns its own slskd container so the
/// track-level source is part of this stack and never touches another project's client.
/// </summary>
public class SlskdConfig
{
    [Description("Enable Stackarr's own Soulseek (slskd) source")]
    public bool Enabled { get; set; } = true;

    [Description("Soulseek account username used by Stackarr's slskd container")]
    public string SoulseekUsername { get; set; } = "";

    [Description("Soulseek account password used by Stackarr's slskd container")]
    public string SoulseekPassword { get; set; } = "";

    [Description("Web/API username for Stackarr's slskd container (the REST API logs in with this). Defaults to slskd's built-in 'slskd' account.")]
    public string ApiUsername { get; set; } = "slskd";

    [Description("Web/API password for Stackarr's slskd container. Defaults to slskd's built-in 'slskd' password.")]
    public string ApiPassword { get; set; } = "slskd";

    [Description("Remove a completed Soulseek transfer (and its downloaded copy) once it has been imported into the library. Soulseek has no seeding obligation for downloads, so leaving them only clutters the list.")]
    public bool RemoveCompletedAfterImport { get; set; } = true;

    [Description("Maximum concurrent Soulseek downloads (slskd download slots). Soulseek also queues per-peer, so higher helps when many peers are available.")]
    public int DownloadSlots { get; set; } = 20;

    [Description("Maximum concurrent Soulseek uploads (slskd upload slots).")]
    public int UploadSlots { get; set; } = 20;

    [Description("Remove failed Soulseek transfers (and their partial files) from the download list once they are this many hours old. slskd keeps failed transfers forever otherwise.")]
    public int FailedTransferRetentionHours { get; set; } = 4;

    [Description("Maximum concurrent Soulseek searches Stackarr runs. Higher enqueues grabs faster; slskd can drop the connection if this is too high.")]
    public int MaxConcurrentSearches { get; set; } = 4;

    [Description("Route slskd's Soulseek connection through the VPN (Gluetun) container. Use when the direct connection to Soulseek is unreliable/blocked; the VPN gives a different, usually cleaner path. Provisioning the tunnel is implied by this flag.")]
    public bool Vpn { get; set; }
}
