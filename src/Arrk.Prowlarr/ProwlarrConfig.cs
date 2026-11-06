using System.ComponentModel;

namespace Arrk.Prowlarr.Configuration;

public class ProwlarrConfig
{
    [Description("Expose Prowlarr through the reverse proxy at /prowlarr")]
    public bool Proxy { get; set; }

    [Description("Cloudflare/DDoS challenge solving for protected indexers")]
    public SolverConfig Solver { get; set; } = new();

    [Description("List of indexer entries to configure in Prowlarr")]
    public List<IndexerDefinition> Entries { get; set; } = [];
}

public class IndexerDefinition
{
    [Description("Indexer display name")]
    public string Name { get; set; } = "";

    [Description("Indexer identifier: the Cardigann definitionFile (e.g. thepiratebay, nyaasi, 1337x) or, for native indexers, the implementation name (e.g. RuTracker)")]
    public string Implementation { get; set; } = "";

    [Description("Whether this indexer is active")]
    public bool Enabled { get; set; } = true;

    [Description("Stop seeding when ratio reaches this value")]
    public double SeedRatio { get; set; } = 2.0;

    [Description("Maximum queries per day (0 = unlimited)")]
    public int QueryLimit { get; set; }

    [Description("Indexer priority (lower = higher priority)")]
    public int Priority { get; set; } = 25;

    [Description("Base URL for the indexer site. For Cardigann indexers this must be one of the mirror URLs the Prowlarr definition supports (the options Prowlarr shows in the indexer's Base Url dropdown); an unsupported mirror breaks category mapping and results get dropped.")]
    public string? Url { get; set; }

    [Description("Torrent categories this indexer supports")]
    public string[] Categories { get; set; } = [];

    [Description("Prowlarr tags. A 'cf' tag routes the indexer through the configured Cloudflare solver (Byparr).")]
    public string[] Tags { get; set; } = [];

    [Description("Search capabilities")]
    public IndexerSearchConfig Search { get; set; } = new();

    [Description("Implementation-specific settings (username, password, custom URLs, etc.)")]
    public Dictionary<string, object> Settings { get; set; } = new();
}

public class IndexerSearchConfig
{
    [Description("Enable general search")]
    public bool Search { get; set; } = true;

    [Description("Enable freeleech-only search")]
    public bool Freeleech { get; set; }

    [Description("Enable movie search")]
    public bool MovieSearch { get; set; }

    [Description("Enable TV search")]
    public bool TvSearch { get; set; }

    [Description("Enable music search")]
    public bool MusicSearch { get; set; }
}

public class IndexerAccessConfig
{
    [Description("If non-empty, only these indexer names are allowed")]
    public string[] Allowlist { get; set; } = [];

    [Description("These indexer names are blocked (ignored if allowlist is set)")]
    public string[] Blocklist { get; set; } = [];
}
