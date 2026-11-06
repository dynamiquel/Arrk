namespace Arrk.Music.Services;

/// <summary>
/// Pure text/genre matching for assistant music search. Kept out of the source so the rules can be
/// unit-tested without a catalogue: a multi-word text query requires every token (plot/keyword
/// search), and genres match either direction so "sci-fi" finds "Science Fiction".
/// </summary>
internal static class MusicAssistantFilters
{
    public static bool MatchesGenre(IEnumerable<string> genres, string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return true;

        string value = genre.Trim();
        return genres.Any(g => g.Contains(value, StringComparison.OrdinalIgnoreCase)
            || value.Contains(g, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MatchesText(string? text, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        string haystack = string.Join(' ', fields.Where(f => !string.IsNullOrWhiteSpace(f)));
        string compact = string.Concat(haystack.Where(char.IsLetterOrDigit));
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (string token in tokens)
        {
            if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                continue;

            string compactToken = string.Concat(token.Where(char.IsLetterOrDigit));
            if (tokens.Length == 1 && compactToken.Length > 2
                && compact.Contains(compactToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static string Normalize(string? value) =>
        new((value ?? "").Where(char.IsLetterOrDigit).Select(c => char.ToLowerInvariant(c)).ToArray());
}
