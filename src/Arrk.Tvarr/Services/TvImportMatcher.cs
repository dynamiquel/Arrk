using Arrk.Tv.Abstractions;
using Arrk.Tvarr.Clients;

namespace Arrk.Tvarr.Services;

/// <summary>
/// One title Arrk thinks an uploaded file could belong to. The score is a 0-100 confidence
/// used only by the confirmation UI and to decide when the AI tie-breaker may help; the arrs'
/// own manual-import parser remains the final authority for the actual import.
/// </summary>
public sealed record TvImportCandidate(
    string MediaType,
    string Title,
    string OriginalTitle,
    int Year,
    int TmdbId,
    int TvdbId,
    string Overview,
    string PosterUrl,
    double Score,
    IReadOnlyList<string> Reasons,
    int? LibraryId,
    bool InLibrary,
    IReadOnlyList<int> SeasonNumbers,
    bool FromArr);

/// <summary>
/// Deterministic release-to-title matcher. It combines release-name parsing with the candidate
/// titles Radarr/Sonarr lookups return, then scores title similarity and year agreement. It is
/// deliberately boring and explainable: the UI shows the score and the reasons, and a human can
/// always override the choice.
/// </summary>
public static class TvImportMatcher
{
    /// <summary>A candidate below this score is only offered as a manual choice, never auto-picked.</summary>
    public const double AutoPickThreshold = 70;

    /// <summary>Two candidates this close are treated as ambiguous by the AI feature.</summary>
    public const int AmbiguityWindow = 8;

    public static IReadOnlyList<TvImportCandidate> RankMovies(
        TvParsedFile parsed,
        IEnumerable<MovieLookupDto> lookupResults,
        IReadOnlyDictionary<int, TvMovie> library)
    {
        List<TvImportCandidate> candidates = [];

        foreach (MovieLookupDto movie in lookupResults)
        {
            TvMovie? existing = movie.TmdbId > 0 && library.TryGetValue(movie.TmdbId, out TvMovie? found)
                ? found
                : null;

            string poster = movie.Images?.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl ?? "";
            List<string> reasons = ["Known title"];
            double score = Score(parsed, movie.Title, movie.OriginalTitle, movie.Year ?? 0, reasons, fromArr: true);
            if (existing is not null)
            {
                score = Math.Min(100, score + 2);
                reasons.Add("Already in your library");
            }

            candidates.Add(new TvImportCandidate(
                "movie",
                movie.Title,
                movie.OriginalTitle,
                movie.Year ?? 0,
                movie.TmdbId,
                0,
                movie.Overview,
                poster,
                score,
                reasons,
                existing?.Id,
                existing is not null,
                [],
                FromArr: true));
        }

        return Deduplicate(candidates);
    }

    public static IReadOnlyList<TvImportCandidate> RankSeries(
        TvParsedFile parsed,
        IEnumerable<SeriesLookupDto> lookupResults,
        IReadOnlyDictionary<int, TvSeries> libraryByTmdb,
        IReadOnlyDictionary<int, TvSeries> libraryByTvdb)
    {
        List<TvImportCandidate> candidates = [];

        foreach (SeriesLookupDto series in lookupResults)
        {
            TvSeries? existing = series.TvdbId > 0 && libraryByTvdb.TryGetValue(series.TvdbId, out TvSeries? byTvdb)
                ? byTvdb
                : series.TmdbId > 0 && libraryByTmdb.TryGetValue(series.TmdbId, out TvSeries? byTmdb) ? byTmdb : null;

            List<int> seasons = series.Seasons
                .Where(season => season.SeasonNumber > 0)
                .Select(season => season.SeasonNumber)
                .OrderBy(number => number)
                .ToList();

            string poster = series.Images?.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl ?? "";
            List<string> reasons = ["Known title"];
            double score = Score(parsed, series.Title, series.OriginalTitle, series.Year ?? 0, reasons, fromArr: true);
            if (existing is not null)
            {
                score = Math.Min(100, score + 2);
                reasons.Add("Already in your library");
            }

            candidates.Add(new TvImportCandidate(
                "tv",
                series.Title,
                series.OriginalTitle,
                series.Year ?? 0,
                series.TmdbId,
                series.TvdbId,
                series.Overview,
                poster,
                score,
                reasons,
                existing?.Id,
                existing is not null,
                seasons,
                FromArr: true));
        }

        return Deduplicate(candidates);
    }

    /// <summary>Adds a candidate the user picked manually (TMDB search) to a ranked list.</summary>
    public static TvImportCandidate ManualCandidate(
        string mediaType,
        string title,
        int year,
        int tmdbId,
        int tvdbId,
        string overview,
        string posterUrl,
        int? libraryId,
        IReadOnlyList<int> seasonNumbers)
    {
        return new TvImportCandidate(
            mediaType,
            title,
            "",
            year,
            tmdbId,
            tvdbId,
            overview,
            posterUrl,
            100,
            ["Chosen by you"],
            libraryId,
            libraryId is > 0,
            seasonNumbers,
            FromArr: false);
    }

    public static double Score(
        TvParsedFile parsed,
        string candidateTitle,
        string candidateOriginalTitle,
        int candidateYear,
        List<string> reasons,
        bool fromArr)
    {
        string parsedTitle = TvImportFilename.Normalize(parsed.CleanTitle);
        string title = TvImportFilename.Normalize(candidateTitle);
        string original = TvImportFilename.Normalize(candidateOriginalTitle);

        double titleScore = Math.Max(
            ScoreText(parsedTitle, title, reasons, "title"),
            ScoreText(parsedTitle, original, reasons, "original title"));

        if (fromArr)
            titleScore = Math.Max(titleScore, 55);

        double score = titleScore;

        if (parsed.Year is int parsedYear && candidateYear > 0)
        {
            int difference = Math.Abs(parsedYear - candidateYear);
            if (difference == 0)
            {
                score += 12;
                reasons.Add($"Year {candidateYear} matches");
            }
            else if (difference == 1)
            {
                score += 6;
                reasons.Add($"Year is within one year ({candidateYear})");
            }
            else if (difference > 2)
            {
                score -= 22;
                reasons.Add($"Year differs ({parsedYear} vs {candidateYear})");
            }
        }

        return Math.Clamp(score, 0, 100);
    }

    static double ScoreText(string parsedTitle, string candidateTitle, List<string> reasons, string label)
    {
        if (parsedTitle.Length == 0 || candidateTitle.Length == 0)
            return 0;

        if (string.Equals(parsedTitle, candidateTitle, StringComparison.Ordinal))
        {
            reasons.Add($"{label} matches exactly");
            return 100;
        }

        if (candidateTitle.Contains(parsedTitle, StringComparison.Ordinal)
            || parsedTitle.Contains(candidateTitle, StringComparison.Ordinal))
        {
            reasons.Add($"{label} contains the release title");
            return 88;
        }

        HashSet<string> parsedTokens = TvImportFilename.Tokens(parsedTitle);
        HashSet<string> candidateTokens = TvImportFilename.Tokens(candidateTitle);
        double overlap = parsedTokens.Count == 0 || candidateTokens.Count == 0
            ? 0
            : (double)parsedTokens.Intersect(candidateTokens).Count() / parsedTokens.Union(candidateTokens).Count();

        double similarity = Similarity(parsedTitle, candidateTitle);

        double score = Math.Max(40 + (overlap * 50), 20 + (similarity * 70));
        if (overlap >= 0.75)
            reasons.Add($"{label} shares most words");
        else if (similarity >= 0.7)
            reasons.Add($"{label} is similar");

        return score;
    }

    /// <summary>Normalised Levenshtein similarity (1 = identical).</summary>
    public static double Similarity(string left, string right)
    {
        if (left == right)
            return 1;
        if (left.Length == 0 || right.Length == 0)
            return 0;

        int distance = Levenshtein(left, right);
        return 1 - ((double)distance / Math.Max(left.Length, right.Length));
    }

    static int Levenshtein(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];

        for (int j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    /// <summary>True when the top two candidates are close enough that a human or model should decide.</summary>
    public static bool IsAmbiguous(IReadOnlyList<TvImportCandidate> candidates)
    {
        if (candidates.Count < 2)
            return false;

        if (candidates[0].Score - candidates[1].Score <= AmbiguityWindow)
            return true;

        return candidates[0].Score < AutoPickThreshold;
    }

    static IReadOnlyList<TvImportCandidate> Deduplicate(List<TvImportCandidate> candidates)
    {
        List<TvImportCandidate> ordered = [.. candidates
            .GroupBy(candidate => candidate.MediaType == "movie"
                ? $"movie:{candidate.TmdbId}"
                : $"tv:{candidate.TvdbId}:{candidate.TmdbId}")
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.InLibrary)
            .ThenBy(candidate => candidate.Title)];

        return ordered;
    }
}
