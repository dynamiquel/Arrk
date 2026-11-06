using System.ComponentModel;
using Arrk.Core.Configuration;
using Arrk.Prowlarr.Configuration;

namespace Arrk.Tvarr;

public class TvConfig
{
    [Description("Enable TV services (Radarr + Sonarr)")]
    public bool Enabled { get; set; } = true;

    [Description("Expose Radarr and Sonarr through the reverse proxy at /radarr and /sonarr")]
    public bool Proxy { get; set; }

    [Description("Quality settings for TV content")]
    public QualitySettings Qualities { get; set; } = new();

    [Description("Quality profile name to use in Radarr and Sonarr")]
    public string QualityProfileName { get; set; } = "Best Available";

    [Description("Indexer allowlist/blocklist for Radarr/Sonarr (empty = all allowed)")]
    public IndexerAccessConfig IndexerSync { get; set; } = new();

    [Description("What to do when a grabbed release fails")]
    public TvFailedDownloadConfig FailedDownloads { get; set; } = new();

    [Description("Periodic searches for monitored content that is still missing")]
    public TvMissingSearchConfig MissingSearch { get; set; } = new();

    [Description("Search actions in the TV UI")]
    public TvSearchConfig Search { get; set; } = new();

    [Description("Local file uploads and manual import")]
    public TvUploadConfig Uploads { get; set; } = new();
}

public class TvSearchConfig
{
    [Description("Show search and retry-search actions. Disable this for a local-only library; upload remains available from the media dialogs and library pages.")]
    public bool Enabled { get; set; } = true;
}

public class TvUploadConfig
{
    [Description("Maximum size of one uploaded file in gigabytes.")]
    public int MaxFileSizeGb { get; set; } = 100;

    [Description("Hours to keep a failed or abandoned upload in the staging area before removing it.")]
    public int RetentionHours { get; set; } = 72;

    [Description("Delete the staged copy after the arr imports the file. The arr uses copy mode so the source stays until it is removed.")]
    public bool DeleteAfterImport { get; set; } = true;
}

public class TvMissingSearchConfig
{
    [Description("Run the hourly search for missing episodes and movies")]
    public bool Enabled { get; set; } = true;
}

public class TvFailedDownloadConfig
{
    [Description("Blocklist a failed release and search for another one. Also keeps 'Redownload Failed' enabled in Radarr/Sonarr.")]
    public bool RetriggerSearch { get; set; } = true;
}
