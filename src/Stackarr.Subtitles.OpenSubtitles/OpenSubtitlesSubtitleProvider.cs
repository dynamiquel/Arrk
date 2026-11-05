using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core.Events;
using Stackarr.Subtitles;
using Stackarr.Tv.Abstractions;

namespace Stackarr.Subtitles.OpenSubtitles;

/// <summary>
/// OpenSubtitles provider. The shared dispatcher owns status, language selection and automatic
/// downloads; this type only searches the OpenSubtitles catalogue and downloads a chosen file.
/// </summary>
public sealed class OpenSubtitlesSubtitleProvider(
    OpenSubtitlesClient client,
    SubtitleFileService files,
    SubtitleLibraryResolver library,
    IOptionsMonitor<OpenSubtitlesConfig> config,
    IEventBus events,
    ILogger<OpenSubtitlesSubtitleProvider> logger) : ISubtitleProvider
{
    public string Name => "opensubtitles";

    public int Priority => 0;

    public bool IsEnabled =>
        config.CurrentValue.Enabled
        && !string.IsNullOrWhiteSpace(config.CurrentValue.ApiKey)
        && library.HasLibraries;

    public Task ConfigureAsync(SubtitlePreferences preferences, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => client.IsAvailableAsync(ct);

    public async Task<IReadOnlyList<SubtitleLanguage>> GetLanguagesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<OpenSubtitlesLanguage> languages = await client.GetLanguagesAsync(ct);
        return languages
            .Where(language => !string.IsNullOrWhiteSpace(language.LanguageCode))
            .Select(language => new SubtitleLanguage(
                SubtitleLanguages.ToTwoLetter(language.LanguageCode),
                string.IsNullOrWhiteSpace(language.LanguageName)
                    ? SubtitleLanguages.DisplayName(language.LanguageCode)
                    : language.LanguageName!))
            .DistinctBy(language => language.Code)
            .OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleSearchRequest request,
        CancellationToken ct = default)
    {
        if (request.Target.Kind is not (SubtitleMediaKind.Movie or SubtitleMediaKind.Episode))
            return [];

        IReadOnlyList<string> languages = request.Languages
            .Select(SubtitleLanguages.ToTwoLetter)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (languages.Count == 0)
            return [];

        OpenSubtitlesSearchQuery? query = await BuildSearchQueryAsync(request.Target, languages, ct);
        if (query is null)
            return [];

        string videoFileName = await GetVideoFileNameAsync(request.Target, ct);
        IReadOnlyList<OpenSubtitlesSubtitle> results;
        try
        {
            results = await client.SearchAsync(query, ct);
        }
        catch (OpenSubtitlesException ex) when (ex.StatusCode is 404 or 406)
        {
            return [];
        }

        List<SubtitleCandidate> candidates = [];
        foreach (OpenSubtitlesSubtitle result in results)
        {
            SubtitleCandidate? candidate = MapCandidate(result, request.Target.Title, videoFileName);
            if (candidate is null)
                continue;

            if (!languages.Any(language => SubtitleLanguages.Matches(language, candidate.LanguageCode)))
                continue;

            candidates.Add(candidate);
        }

        return [.. candidates.OrderByDescending(candidate => candidate.Score)];
    }

    public async Task DownloadAsync(SubtitleDownloadRequest request, CancellationToken ct = default)
    {
        string? path = await ResolvePathAsync(request.Target, ct);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("This media item has no file to attach a subtitle to.");

        if (!int.TryParse(request.Candidate.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileId))
            throw new InvalidOperationException("The selected subtitle has an invalid OpenSubtitles file id.");

        string languageCode = SubtitleLanguages.NormalizeCode(request.Candidate.LanguageCode);
        if (languageCode.Length == 0)
            throw new InvalidOperationException("The selected subtitle has no language code.");

        OpenSubtitlesDownloadResponse download = await client.DownloadAsync(fileId, "srt", ct);
        if (string.IsNullOrWhiteSpace(download.Link))
            throw new InvalidOperationException("OpenSubtitles returned no download link.");

        byte[] payload = await client.GetFileAsync(download.Link, ct);
        await files.WriteAsync(
            path,
            languageCode,
            request.Candidate.Forced,
            request.Candidate.HearingImpaired,
            payload,
            download.FileName,
            overwrite: true,
            ct);

        await events.Publish(new SubtitleDownloadedEvent(
            Name,
            languageCode,
            request.Target.Title,
            request.Target.Subtitle,
            SeriesId: request.Target.SeriesId > 0 ? request.Target.SeriesId : null,
            EpisodeId: request.Target.EpisodeId > 0 ? request.Target.EpisodeId : null,
            MovieId: request.Target.MovieId > 0 ? request.Target.MovieId : null), ct);
    }

    async Task<OpenSubtitlesSearchQuery?> BuildSearchQueryAsync(
        SubtitleTarget target,
        IReadOnlyList<string> languages,
        CancellationToken ct)
    {
        if (target.Kind == SubtitleMediaKind.Movie)
        {
            TvMovie? movie = await library.GetMovieAsync(target.MovieId, ct);
            if (movie is null)
                return null;

            return new OpenSubtitlesSearchQuery
            {
                ImdbId = NormalizeImdbId(movie.ImdbId),
                TmdbId = movie.TmdbId > 0 ? movie.TmdbId : null,
                Query = movie.Title,
                Year = movie.Year > 0 ? movie.Year : null,
                Type = "movie",
                Languages = languages,
                MovieHash = await files.ComputeHashAsync(movie.File?.Path, ct),
                IncludeMachineTranslated = config.CurrentValue.IncludeMachineTranslated
            };
        }

        TvEpisode? episode = await library.GetEpisodeAsync(target.SeriesId, target.EpisodeId, ct);
        if (episode is null)
            return null;

        TvSeries? series = await library.GetSeriesAsync(target.SeriesId, ct);
        if (series is null)
            return null;

        return new OpenSubtitlesSearchQuery
        {
            ParentImdbId = NormalizeImdbId(series.ImdbId),
            ParentTmdbId = series.TmdbId > 0 ? series.TmdbId : null,
            Query = $"{series.Title} S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}",
            SeasonNumber = episode.SeasonNumber,
            EpisodeNumber = episode.EpisodeNumber,
            Type = "episode",
            Languages = languages,
            MovieHash = await files.ComputeHashAsync(episode.File?.Path, ct),
            IncludeMachineTranslated = config.CurrentValue.IncludeMachineTranslated
        };
    }

    SubtitleCandidate? MapCandidate(OpenSubtitlesSubtitle result, string fallbackTitle, string videoFileName)
    {
        OpenSubtitlesAttributes attributes = result.Attributes;
        if (!config.CurrentValue.IncludeMachineTranslated
            && (attributes.MachineTranslated == true || attributes.AiTranslated == true))
        {
            return null;
        }

        string languageCode = SubtitleLanguages.ToTwoLetter(attributes.Language);
        if (languageCode.Length == 0)
            return null;

        OpenSubtitlesFile? file = attributes.Files
            .OrderByDescending(item => Path.GetExtension(item.FileName ?? "").Equals(".srt", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (file is null || file.FileId <= 0)
            return null;

        string release = attributes.Release?.Trim() ?? "";
        string fileName = file.FileName?.Trim() ?? "";
        string title = release.Length > 0
            ? release
            : fileName.Length > 0
                ? fileName
                : attributes.FeatureDetails?.Title ?? fallbackTitle;

        List<string> releaseInfo = [];
        if (release.Length > 0)
            releaseInfo.Add(release);
        if (fileName.Length > 0 && !string.Equals(fileName, release, StringComparison.OrdinalIgnoreCase))
            releaseInfo.Add(fileName);

        return new SubtitleCandidate
        {
            Provider = Name,
            Id = file.FileId.ToString(CultureInfo.InvariantCulture),
            LanguageCode = languageCode,
            LanguageName = SubtitleLanguages.DisplayName(languageCode),
            Title = title,
            ReleaseInfo = releaseInfo,
            Score = ScoreCandidate(attributes, videoFileName),
            Forced = attributes.ForeignPartsOnly,
            HearingImpaired = attributes.HearingImpaired,
            OriginalFormat = false,
            Uploader = attributes.Uploader?.Name ?? ""
        };
    }

    async Task<string> GetVideoFileNameAsync(SubtitleTarget target, CancellationToken ct)
    {
        if (target.Kind == SubtitleMediaKind.Movie)
        {
            TvMovie? movie = await library.GetMovieAsync(target.MovieId, ct);
            return Path.GetFileName(movie?.File?.Path ?? "");
        }

        TvEpisode? episode = await library.GetEpisodeAsync(target.SeriesId, target.EpisodeId, ct);
        return Path.GetFileName(episode?.File?.Path ?? "");
    }

    async Task<string?> ResolvePathAsync(SubtitleTarget target, CancellationToken ct)
    {
        if (target.Kind == SubtitleMediaKind.Movie)
        {
            TvMovie? movie = await library.GetMovieAsync(target.MovieId, ct);
            return movie?.File?.Path;
        }

        TvEpisode? episode = await library.GetEpisodeAsync(target.SeriesId, target.EpisodeId, ct);
        return episode?.File?.Path;
    }

    static int ScoreCandidate(OpenSubtitlesAttributes attributes, string videoFileName)
    {
        int score = 50;
        if (attributes.MoviehashMatch)
            score += 30;

        if (attributes.Ratings > 0)
            score += (int)Math.Round(attributes.Ratings);

        if (attributes.DownloadCount > 0)
            score += Math.Min(5, attributes.DownloadCount / 2000);

        if (string.Equals(attributes.Format, "srt", StringComparison.OrdinalIgnoreCase)
            || attributes.Files.Any(file => (file.FileName ?? "").EndsWith(".srt", StringComparison.OrdinalIgnoreCase)))
        {
            score += 5;
        }

        score += (int)Math.Round(ReleaseOverlap(videoFileName, attributes.Release) * 15);

        if (attributes.MachineTranslated == true || attributes.AiTranslated == true)
            score -= 25;

        return Math.Clamp(score, 0, 100);
    }

    static double ReleaseOverlap(string fileName, string? release)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(release))
            return 0;

        HashSet<string> left = Tokens(fileName);
        HashSet<string> right = Tokens(release);
        if (left.Count == 0 || right.Count == 0)
            return 0;

        int shared = left.Count(right.Contains);
        return shared / (double)Math.Max(left.Count, right.Count);
    }

    static HashSet<string> Tokens(string value) =>
        [.. value
            .ToLowerInvariant()
            .Split(['.', '_', '-', ' ', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1 && !token.All(char.IsDigit))];

    static string? NormalizeImdbId(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
            return null;

        string value = imdbId.Trim();
        if (value.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return value.Length > 0 && value.All(char.IsDigit) ? value : null;
    }
}
