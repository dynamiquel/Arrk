using System.Globalization;

namespace Stackarr.Subtitles.Abstractions;

/// <summary>The media a subtitle request applies to.</summary>
public enum SubtitleMediaKind
{
    Movie,
    Episode,
    Season,
    Series
}

/// <summary>
/// A movie, episode, season or series in the media library. Ids are the library manager's ids
/// (Radarr movie id / Sonarr series and episode ids), which keeps the contract independent of a
/// subtitle backend and of Stackarr's UI models.
/// </summary>
public sealed record SubtitleTarget
{
    public required SubtitleMediaKind Kind { get; init; }

    public int MovieId { get; init; }

    public int SeriesId { get; init; }

    public int EpisodeId { get; init; }

    public int SeasonNumber { get; init; }

    /// <summary>Title shown in the request UI, e.g. the movie title or episode title.</summary>
    public string Title { get; init; } = "";

    /// <summary>Secondary line shown under the title, e.g. the series name or "S01E02".</summary>
    public string Subtitle { get; init; } = "";

    public static SubtitleTarget Movie(int movieId, string title = "", string subtitle = "") =>
        new() { Kind = SubtitleMediaKind.Movie, MovieId = movieId, Title = title, Subtitle = subtitle };

    public static SubtitleTarget Episode(
        int seriesId,
        int episodeId,
        int seasonNumber,
        string title = "",
        string subtitle = "") =>
        new()
        {
            Kind = SubtitleMediaKind.Episode,
            SeriesId = seriesId,
            EpisodeId = episodeId,
            SeasonNumber = seasonNumber,
            Title = title,
            Subtitle = subtitle
        };

    public static SubtitleTarget Season(int seriesId, int seasonNumber, string title = "", string subtitle = "") =>
        new()
        {
            Kind = SubtitleMediaKind.Season,
            SeriesId = seriesId,
            SeasonNumber = seasonNumber,
            Title = title,
            Subtitle = subtitle
        };

    public static SubtitleTarget Series(int seriesId, string title = "", string subtitle = "") =>
        new() { Kind = SubtitleMediaKind.Series, SeriesId = seriesId, Title = title, Subtitle = subtitle };
}

/// <summary>A subtitle language in ISO 639-1 (alpha-2) form.</summary>
public sealed record SubtitleLanguage(string Code, string Name);

/// <summary>An existing subtitle file (or embedded track) for a target.</summary>
public sealed record SubtitleTrack(
    string LanguageCode,
    string LanguageName,
    bool Forced,
    bool HearingImpaired,
    string Path);

/// <summary>
/// What a target currently has and what it is missing. Series and season targets aggregate their
/// episodes so one popup can describe the whole scope.
/// </summary>
public sealed record SubtitleStatus(
    IReadOnlyList<SubtitleTrack> Available,
    IReadOnlyList<SubtitleLanguage> Missing,
    int EpisodeCount = 0,
    int MissingEpisodeCount = 0);

/// <summary>A candidate returned by a manual subtitle search.</summary>
public sealed record SubtitleCandidate
{
    public required string Provider { get; init; }

    /// <summary>Provider-specific subtitle id, passed back verbatim when downloading.</summary>
    public required string Id { get; init; }

    public string LanguageCode { get; init; } = "";

    public string LanguageName { get; init; } = "";

    public string Title { get; init; } = "";

    public IReadOnlyList<string> ReleaseInfo { get; init; } = [];

    public int Score { get; init; }

    public bool Forced { get; init; }

    public bool HearingImpaired { get; init; }

    public bool OriginalFormat { get; init; }

    public string Uploader { get; init; } = "";
}

public sealed record SubtitleSearchRequest(
    SubtitleTarget Target,
    IReadOnlyList<string> Languages,
    bool IncludeForced = false);

public sealed record SubtitleDownloadRequest(
    SubtitleTarget Target,
    SubtitleCandidate Candidate);

/// <summary>
/// An automatic request: grab missing subtitles for every file in the target. A provider may do
/// this through its own missing-subtitle search (preferred) so new episodes keep the same profile.
/// </summary>
public sealed record SubtitleRequest(
    SubtitleTarget Target,
    IReadOnlyList<string> Languages);

/// <summary>Defaults Stackarr applies to a subtitle backend.</summary>
public sealed record SubtitlePreferences(
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Providers,
    bool AutoDownload);

/// <summary>
/// The subtitle backend seam. A backend can be replaced without changing the shared subtitles UI,
/// the TV movie/episode dialogs or the player integration.
/// </summary>
public interface ISubtitleProvider
{
    string Name { get; }

    /// <summary>Lower values win when more than one backend is enabled.</summary>
    int Priority => 100;

    /// <summary>Whether this backend is enabled and configured enough to serve requests.</summary>
    bool IsEnabled { get; }

    Task ConfigureAsync(SubtitlePreferences preferences, CancellationToken ct = default);

    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SubtitleLanguage>> GetLanguagesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        CancellationToken ct = default);

    Task DownloadAsync(SubtitleDownloadRequest request, CancellationToken ct = default);
}

/// <summary>Shared language rules for every subtitle implementation.</summary>
public static class SubtitleLanguages
{
    /// <summary>English is the shipped default, but it is not force-added when configuration omits it.</summary>
    public const string English = "en";

    /// <summary>
    /// ISO 639-2/B codes used by some media containers and library managers. .NET's cultures use
    /// the 639-2/T forms (fra, deu, nld, ...), so the bibliographic variants need an explicit map.
    /// </summary>
    static readonly Dictionary<string, string> BibliographicCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sq",
        ["arm"] = "hy",
        ["baq"] = "eu",
        ["bur"] = "my",
        ["chi"] = "zh",
        ["cze"] = "cs",
        ["dut"] = "nl",
        ["fre"] = "fr",
        ["geo"] = "ka",
        ["ger"] = "de",
        ["gre"] = "el",
        ["ice"] = "is",
        ["mac"] = "mk",
        ["may"] = "ms",
        ["mol"] = "ro",
        ["per"] = "fa",
        ["rum"] = "ro",
        ["scc"] = "sr",
        ["scr"] = "hr",
        ["slo"] = "sk",
        ["tib"] = "bo",
        ["wel"] = "cy"
    };

    /// <summary>
    /// Returns the configured languages with duplicates removed. Invalid or blank entries are
    /// ignored. English is only present when the configuration explicitly includes it (or leaves
    /// the default configuration in place).
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? configured)
    {
        List<string> languages = [];

        if (configured is not null)
        {
            foreach (string language in configured)
            {
                string code = NormalizeCode(language);
                if (code.Length == 0 || languages.Contains(code, StringComparer.OrdinalIgnoreCase))
                    continue;
                languages.Add(code);
            }
        }

        return languages;
    }

    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "";

        string value = code.Trim().ToLowerInvariant();
        int separator = value.IndexOfAny(['-', '_']);
        if (separator > 0)
            value = value[..separator];

        return value;
    }

    /// <summary>Normalizes configured languages to the two-letter form used by every backend.</summary>
    public static IReadOnlyList<string> NormalizeTwoLetter(IEnumerable<string>? configured) =>
        [.. Normalize(configured)
            .Select(ToTwoLetter)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Converts an ISO 639-1, 639-2/T or 639-2/B code to the two-letter form used by the UI and
    /// config. Unknown codes are returned normalized so callers can still compare them.
    /// </summary>
    public static string ToTwoLetter(string? code)
    {
        string normalized = NormalizeCode(code);
        if (normalized.Length == 2)
            return normalized;

        if (normalized.Length == 3)
        {
            if (BibliographicCodes.TryGetValue(normalized, out string? mapped))
                return mapped;

            CultureInfo? culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(item => string.Equals(
                    item.ThreeLetterISOLanguageName, normalized, StringComparison.OrdinalIgnoreCase));
            if (culture is not null && culture.TwoLetterISOLanguageName.Length == 2)
                return culture.TwoLetterISOLanguageName;
        }

        return normalized;
    }

    public static bool Matches(string? left, string? right)
    {
        string a = ToTwoLetter(left);
        string b = ToTwoLetter(right);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Human-readable English name for an ISO 639-1 code, falling back to the input.</summary>
    public static string DisplayName(string? code)
    {
        string normalized = ToTwoLetter(code);
        if (normalized.Length == 0)
            return "";

        CultureInfo? culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(item => string.Equals(
                item.TwoLetterISOLanguageName, normalized, StringComparison.OrdinalIgnoreCase));
        return culture is null || string.IsNullOrWhiteSpace(culture.EnglishName) ? normalized : culture.EnglishName;
    }
}
