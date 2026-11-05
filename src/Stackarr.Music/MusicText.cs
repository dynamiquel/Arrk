using System.Text.RegularExpressions;

namespace Stackarr.Music;

/// <summary>
/// Shared text normalisation for music matching. Deezer appends disambiguation tails to titles
/// (<c>Let It Go (From "Frozen"/Soundtrack Version)</c>, <c>Song (Remastered 2011)</c>) and releases
/// carry edition/format noise (<c>... [FLAC] 320</c>), so both the acquisition scorer and the lyrics
/// matcher compare titles through these helpers instead of each rolling its own.
/// </summary>
internal static class MusicText
{
    /// <summary>Punctuation-insensitive comparison key: letters and digits only, lowercased.</summary>
    public static string NormalizeKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Strips release junk (years, formats, edition tags) so a messy folder or a
    /// Deezer-edition title can still be matched to a clean metadata title.</summary>
    public static string CleanReleaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        // Junk keywords that mark a bracketed group as an edition/format, not part of the title.
        const string junk = "flac|mp3|m4a|aac|wav|alac|ape|wv|320|256|192|vbr|kbps|khz|hz|bit|hires|web|webrip|webdl|cd|cds|vinyl|disc|disk|remaster(?:ed)?|deluxe|edition|expanded|anniversary|special|bonus|collector|reissue|mono|stereo|remix|version|mix|super|scene|pmedia|ep|single|explicit|clean|international";

        string s = name;
        // Strip any bracketed group that contains a junk keyword (and a bare-year group).
        s = Regex.Replace(s, @"[\(\[]\s*(?:19|20)\d{2}\s*[\)\]]", " ");
        s = Regex.Replace(s, $@"[\(\[]\s*(?:[^\)\]]*\b(?:{junk})\b[^\)\]]*|(?:19|20)\d{{2}})[^\)\]]*[\)\]]", " ", RegexOptions.IgnoreCase);
        // Bare years and loose junk words.
        s = Regex.Replace(s, @"\b(?:19|20)\d{2}\b", " ");
        s = Regex.Replace(s, $@"\b(?:{junk})\b", " ", RegexOptions.IgnoreCase);
        // Ordinals like "40th"/"50th" left over from "40th Anniversary".
        s = Regex.Replace(s, @"\b\d{1,3}(?:st|nd|rd|th)\b", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '_', '.', '[', ']', '(', ')');
        return s;
    }

    /// <summary>
    /// The title to search indexers, Soulseek and lyrics providers with. Drops the trailing
    /// parenthetical groups Deezer adds, then reuses the album cleaner for any leftover
    /// edition/format noise. Falls back to the original when cleaning would leave nothing (e.g. the
    /// song is literally "1999").
    /// </summary>
    public static string CleanTrackTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        string cleaned = title.Trim();
        string previous;
        do
        {
            previous = cleaned;
            cleaned = Regex.Replace(cleaned, @"\s*[\(\[]\s*[^\(\)\[\]]*\s*[\)\]]\s*$", "").Trim();
        }
        while (cleaned.Length > 0 && cleaned != previous);

        // Fall back to the bracket-stripped form when the album cleaner empties it out entirely
        // (e.g. "1989 (Taylor's Version)" → "1989"), only reverting to the raw title when even that
        // is empty (e.g. the song is literally "(Untitled)").
        string withoutJunk = CleanReleaseName(cleaned);
        if (!string.IsNullOrWhiteSpace(withoutJunk))
            return withoutJunk;
        return cleaned.Length > 0 ? cleaned : title.Trim();
    }

    /// <summary>True when two titles refer to the same thing, ignoring release junk
    /// ("Hotel California" vs "Hotel California (2013 Remaster)").</summary>
    public static bool TitleMatches(string a, string b)
    {
        string ka = NormalizeKey(CleanReleaseName(a));
        string kb = NormalizeKey(CleanReleaseName(b));
        if (ka.Length == 0 || kb.Length == 0)
            return false;
        return ka == kb
            || ka.Contains(kb, StringComparison.Ordinal)
            || kb.Contains(ka, StringComparison.Ordinal);
    }
}
