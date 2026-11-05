using Stackarr.Core.Models;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// Maps an arr release resource (<c>GET /api/v3/release</c>) to the shared, domain-agnostic
/// <see cref="ReleaseCandidate"/> the interactive-search picker renders. Format chips are derived
/// from the release name so they line up with the qualities Stackarr's TV profile scores on
/// (Dolby Vision, HDR flavours, x265/x264, Atmos/TrueHD/DTS, source).
/// </summary>
public static class ArrReleaseMapper
{
    public static ReleaseCandidate ToCandidate(ReleaseResourceDto release)
    {
        return new ReleaseCandidate
        {
            Id = string.IsNullOrWhiteSpace(release.Guid) ? release.Title : release.Guid,
            Title = release.Title,
            Indexer = release.Indexer,
            Protocol = string.IsNullOrWhiteSpace(release.Protocol) ? "torrent" : release.Protocol,
            Size = release.Size,
            Seeders = release.Seeders,
            Leechers = release.Leechers,
            PublishDate = release.PublishDate ?? DateTime.MinValue,
            Score = release.CustomFormatScore,
            QualityLabel = release.Quality?.Quality?.Name ?? "",
            FormatTags = BuildTags(release.Title),
            Rejected = release.Rejected,
            Rejections = release.Rejections ?? [],
            IndexerId = release.IndexerId,
            Guid = release.Guid,
            InfoHash = release.InfoHash,
            DownloadUrl = release.DownloadUrl
        };
    }

    /// <summary>Curated, ordered format tags for a release name; the most specific match wins.</summary>
    public static IReadOnlyList<string> BuildTags(string title)
    {
        string t = Normalize(title);
        List<string> tags = [];

        // Dynamic range.
        if (Contains(t, "dolby vision", "dovi", " dv ")) tags.Add("Dolby Vision");
        if (Contains(t, "hdr10+", "hdr10 plus")) tags.Add("HDR10+");
        else if (Contains(t, "hdr10")) tags.Add("HDR10");
        else if (Contains(t, "hdr")) tags.Add("HDR");
        if (Contains(t, "hlg")) tags.Add("HLG");

        // Video codec.
        if (Contains(t, "x265", "h265", "hevc")) tags.Add("x265");
        else if (Contains(t, "x264", "h264", "avc")) tags.Add("x264");
        else if (Contains(t, "av1")) tags.Add("AV1");

        // Audio (most specific wins); Atmos is additive.
        if (Contains(t, "atmos")) tags.Add("Atmos");
        if (Contains(t, "dts x", "dtsx", "dts:x")) tags.Add("DTS:X");
        else if (Contains(t, "dts hd", "dtshd")) tags.Add("DTS-HD");
        else if (Contains(t, "dts")) tags.Add("DTS");
        else if (Contains(t, "truehd", "true hd")) tags.Add("TrueHD");
        else if (Contains(t, "ddp", "eac3", "dd+", "dolby digital plus")) tags.Add("DD+");
        else if (Contains(t, "ac3", "dd5", "dolby digital")) tags.Add("DD");
        else if (Contains(t, "flac")) tags.Add("FLAC");
        else if (Contains(t, "aac")) tags.Add("AAC");

        // Source.
        if (Contains(t, "remux")) tags.Add("REMUX");
        if (Contains(t, "bluray", "blu ray", "bdrip", "brrip")) tags.Add("BluRay");
        else if (Contains(t, "web dl", "webdl")) tags.Add("WEB-DL");
        else if (Contains(t, "web rip", "webrip")) tags.Add("WEBRip");

        if (Contains(t, "repack")) tags.Add("REPACK");
        else if (Contains(t, "proper")) tags.Add("PROPER");
        if (Contains(t, "10bit", "10 bit")) tags.Add("10bit");
        if (Contains(t, " 3d ")) tags.Add("3D");

        return tags;
    }

    /// <summary>
    /// Lower-cases the name and turns common release-name separators into spaces, so token matches
    /// work on ".DV." / "DTS-HD" / "WEB-DL" alike. '+' is kept so "HDR10+" stays intact.
    /// </summary>
    static string Normalize(string title)
    {
        char[] separators = ['.', '_', '-', '[', ']', '(', ')', ',', '/', '\\', '~', '|'];
        System.Text.StringBuilder sb = new(" ");
        foreach (char c in title.ToLowerInvariant())
            sb.Append(separators.Contains(c) ? ' ' : c);
        sb.Append(' ');
        return sb.ToString();
    }

    static bool Contains(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));
}
