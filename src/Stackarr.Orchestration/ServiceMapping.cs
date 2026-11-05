namespace Stackarr.Orchestration;

public static class ServiceMapping
{
    public static readonly Dictionary<string, string> ConceptualToDisplayName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tv"] = "TV",
        ["music"] = "Music",
        ["indexer"] = "Indexer",
        ["torrent_downloader"] = "Downloads",
        ["bazarr"] = "Subtitles",
        ["jellyfin"] = "Jellyfin",
        ["navidrome"] = "Navidrome",
        ["vpn"] = "VPN",
        ["reverse_proxy"] = "Proxy",
    };

    public static string GetDisplayName(string conceptualName)
    {
        return ConceptualToDisplayName.TryGetValue(conceptualName, out string? display) ? display : conceptualName;
    }
}
