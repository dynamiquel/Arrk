using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Stackarr.Tvarr.Services;

/// <summary>What a release file name tells us before an arr is asked to parse it.</summary>
public sealed record TvParsedFile(
    string FileName,
    string CleanTitle,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? EndEpisodeNumber,
    bool IsSeasonPack)
{
    public bool IsMultiEpisode => EndEpisodeNumber is int end && EpisodeNumber is int start && end > start;

    public string EpisodeLabel
    {
        get
        {
            if (SeasonNumber is not int season || EpisodeNumber is not int episode)
                return "";

            return IsMultiEpisode
                ? $"S{season:00}E{episode:00}-E{EndEpisodeNumber:00}"
                : $"S{season:00}E{episode:00}";
        }
    }
}

/// <summary>
/// Best-effort release-name parsing used for matching and for showing the user what Stackarr
/// thinks a file is. Radarr/Sonarr remain the authority when they can parse the file themselves;
/// this parser is the deterministic fallback and the input to the AI tie-breaker.
/// </summary>
public static class TvImportFilename
{
    static readonly Regex SeasonEpisode = new(
        @"(?<![a-z0-9])s(?<season>\d{1,2})[ ._-]?e(?<episode>\d{1,3})(?:[ ._-]?e?(?<end>\d{1,3}))?(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex CrossEpisode = new(
        @"(?<![a-z0-9])(?<season>\d{1,2})x(?<episode>\d{1,3})(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex SeasonOnly = new(
        @"(?<![a-z0-9])s(?:eason)?[ ._-]?(?<season>\d{1,2})(?![ ._-]?e\d)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex EpisodeOnly = new(
        @"(?<![a-z0-9])e(?:p|pisode)?[ ._-]?(?<episode>\d{1,3})(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex AbsoluteEpisode = new(
        @"\s-\s*(?<episode>\d{1,4})(?:v\d)?(?=\s|\[|\(|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex Year = new(
        @"(?<![a-z0-9])(?<year>(?:19|20)\d{2})(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex ReleaseTag = new(
        @"(?<![a-z0-9])(?:2160p|1080p|720p|576p|480p|4k|uhd|bluray|blu-ray|bdrip|brrip|webrip|web-dl|webdl|web|hdtv|dvdrip|dvd|remux|hdr10\+?|hdr10|hdr|dolby[ ._-]?vision|dovi|dv|x264|x265|h264|h265|hevc|avc|aac|ac3|eac3|ddp?5[ ._-]?1|dd\+?|dts[ ._-]?hd|dts[ ._-]?x|dts|truehd|atmos|opus|flac|mp3|multi|dual[ ._-]?audio|ita|eng|fre|fra|spa|ger|deu|jpn|jap|kor|chi|rus|subbed|dubbed|sub|dub)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex LeadingGroup = new(
        @"^\[[^\]]{1,40}\]\s*",
        RegexOptions.Compiled);

    static readonly Regex TrailingGroup = new(
        @"\s*-[a-z0-9]{2,24}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly HashSet<string> IgnoredReleaseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ita", "eng", "fre", "fra", "spa", "ger", "deu", "jpn", "jap", "kor", "chi", "rus",
        "multi", "dual", "audio", "sub", "subs", "subbed", "dub", "dubbed"
    };

    static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "of", "or", "to", "in", "on", "at", "by", "for", "with"
    };

    public static TvParsedFile Parse(string fileName)
    {
        string leaf = Path.GetFileName(fileName.Replace('\\', '/'));
        string baseName = Path.GetFileNameWithoutExtension(leaf);
        string working = baseName.Trim();

        while (LeadingGroup.IsMatch(working))
            working = LeadingGroup.Replace(working, "", 1).Trim();

        int cut = working.Length;

        (int? season, int? episode, int? end, bool seasonPack) = ParseEpisode(working, ref cut);
        (int? year, int yearCut) = ParseYear(working);
        if (yearCut >= 0 && yearCut < cut)
            cut = yearCut;

        Match tag = ReleaseTag.Match(working);
        if (tag.Success && tag.Index < cut)
            cut = tag.Index;

        // A bracket after the title is usually a release tag group; a bracket before the title
        // was already stripped as a release group. Cut at the first remaining bracket.
        int bracket = working.IndexOfAny(['[', ']']);
        if (bracket >= 0 && bracket < cut)
            cut = bracket;

        string titleRegion = cut > 0 && cut <= working.Length ? working[..cut] : working;
        string title = CleanTitle(titleRegion);

        // A pure year is a title ("2012"), not a release year.
        if (title.Length == 0)
        {
            year = null;
            title = CleanTitle(working);
        }

        return new TvParsedFile(
            leaf,
            title,
            year,
            season,
            episode,
            end,
            seasonPack);
    }

    static (int? Season, int? Episode, int? End, bool SeasonPack) ParseEpisode(string working, ref int cut)
    {
        Match match = SeasonEpisode.Match(working);
        if (match.Success)
        {
            if (match.Index < cut)
                cut = match.Index;

            int season = int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture);
            int episode = int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture);
            int? end = match.Groups["end"].Success
                ? int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)
                : null;
            return (season, episode, end > episode ? end : null, false);
        }

        match = CrossEpisode.Match(working);
        if (match.Success)
        {
            if (match.Index < cut)
                cut = match.Index;

            return (
                int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture),
                null,
                false);
        }

        match = SeasonOnly.Match(working);
        if (match.Success)
        {
            if (match.Index < cut)
                cut = match.Index;

            return (
                int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture),
                null,
                null,
                true);
        }

        match = EpisodeOnly.Match(working);
        if (match.Success)
        {
            if (match.Index < cut)
                cut = match.Index;

            return (
                null,
                int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture),
                null,
                false);
        }

        match = AbsoluteEpisode.Match(working);
        if (match.Success)
        {
            if (match.Index < cut)
                cut = match.Index;

            return (
                null,
                int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture),
                null,
                false);
        }

        return (null, null, null, false);
    }

    static (int? Year, int Cut) ParseYear(string working)
    {
        int? selected = null;
        int selectedIndex = -1;
        int currentYear = DateTime.UtcNow.Year;

        foreach (Match match in Year.Matches(working))
        {
            int value = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            if (value is < 1900 or > 2100 || value > currentYear + 2)
                continue;

            selected = value;
            selectedIndex = match.Index;
        }

        if (selected is null)
            return (null, -1);

        // "1917.2019.1080p": the last valid year is the release year and the earlier one is the
        // title. If the chosen year is the first token and the remaining text has no title, treat
        // it as a title instead.
        string before = working[..selectedIndex];
        string after = working[(selectedIndex + 4)..];
        if (CleanTitle(before).Length == 0 && CleanTitle(after).Length == 0)
            return (null, -1);

        return (selected, selectedIndex);
    }

    /// <summary>Normalises a title fragment for display and matching.</summary>
    public static string CleanTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string text = value.Trim().Replace('.', ' ').Replace('_', ' ');
        text = TrailingGroup.Replace(text, "");
        StringBuilder builder = new(text.Length);

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
            else
                builder.Append(' ');
        }

        List<string> tokens = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !IgnoredReleaseTokens.Contains(token))
            .ToList();

        return string.Join(' ', tokens).Trim();
    }

    /// <summary>
    /// Comparison form: lower case, accent-free, "and" for an ampersand and punctuation removed.
    /// </summary>
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string decomposed = value.Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);

        foreach (char c in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (c is '&')
            {
                builder.Append(" and ");
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Token set used for a forgiving title overlap score.</summary>
    public static HashSet<string> Tokens(string value)
    {
        string normalized = Normalize(value);
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !IgnoredReleaseTokens.Contains(token) && !StopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }
}
