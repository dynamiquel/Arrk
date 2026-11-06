using Microsoft.Extensions.Options;
using Arrk.Media.Abstractions;
using Arrk.Tv.Abstractions;

namespace Arrk.Subtitles;

/// <summary>
/// Provider-independent status: scans sidecars and embedded tracks once and reports what is
/// missing for the configured languages. Every provider shares this result.
/// </summary>
public sealed class SubtitleStatusService(
    SubtitleFileService files,
    SubtitleLibraryResolver library,
    IOptionsMonitor<SubtitlesConfig> config)
{
    public async Task<SubtitleStatus?> GetStatusAsync(SubtitleTarget target, CancellationToken ct = default)
    {
        switch (target.Kind)
        {
            case SubtitleMediaKind.Movie:
            {
                TvMovie? movie = await library.GetMovieAsync(target.MovieId, ct);
                return movie is null ? null : await BuildMovieStatusAsync(movie, ct);
            }
            case SubtitleMediaKind.Episode:
            {
                TvEpisode? episode = await library.GetEpisodeAsync(target.SeriesId, target.EpisodeId, ct);
                return episode is null ? null : await BuildEpisodeStatusAsync(episode, ct);
            }
            case SubtitleMediaKind.Series:
            case SubtitleMediaKind.Season:
            {
                List<TvEpisode> episodes = await library.GetEpisodesAsync(target.SeriesId, ct);
                if (target.Kind == SubtitleMediaKind.Season)
                    episodes = episodes.Where(episode => episode.SeasonNumber == target.SeasonNumber).ToList();

                episodes = episodes.Where(episode => episode.HasFile).ToList();
                return episodes.Count == 0 ? null : await BuildAggregateStatusAsync(episodes, ct);
            }
            default:
                return null;
        }
    }

    public async Task<List<TvEpisode>> GetEpisodesAsync(
        int seriesId,
        int? seasonNumber,
        CancellationToken ct = default)
    {
        List<TvEpisode> episodes = await library.GetEpisodesAsync(seriesId, ct);
        if (seasonNumber is int season)
            episodes = episodes.Where(episode => episode.SeasonNumber == season).ToList();
        return episodes.Where(episode => episode.HasFile).ToList();
    }

    async Task<SubtitleStatus> BuildMovieStatusAsync(TvMovie movie, CancellationToken ct)
    {
        IReadOnlyList<SubtitleTrack> tracks = await files.GetTracksAsync(
            movie.File?.Path,
            movie.File?.Technical,
            ct);
        return BuildStatus(tracks, episodeCount: 1);
    }

    async Task<SubtitleStatus> BuildEpisodeStatusAsync(TvEpisode episode, CancellationToken ct)
    {
        IReadOnlyList<SubtitleTrack> tracks = await files.GetTracksAsync(
            episode.File?.Path,
            episode.File?.Technical,
            ct);
        return BuildStatus(tracks, episodeCount: 1);
    }

    async Task<SubtitleStatus> BuildAggregateStatusAsync(List<TvEpisode> episodes, CancellationToken ct)
    {
        List<SubtitleTrack> available = [];
        List<SubtitleLanguage> missing = [];
        int missingEpisodes = 0;

        foreach (TvEpisode episode in episodes)
        {
            IReadOnlyList<SubtitleTrack> tracks = await files.GetTracksAsync(
                episode.File?.Path,
                episode.File?.Technical,
                ct,
                probeEmbedded: false);
            available.AddRange(tracks);

            List<SubtitleLanguage> episodeMissing = MissingLanguages(tracks);
            if (episodeMissing.Count > 0)
                missingEpisodes++;
            missing.AddRange(episodeMissing);
        }

        return new SubtitleStatus(
            available
                .DistinctBy(track => (track.LanguageCode, track.Forced, track.HearingImpaired))
                .ToList(),
            missing
                .DistinctBy(language => language.Code)
                .ToList(),
            episodes.Count,
            missingEpisodes);
    }

    SubtitleStatus BuildStatus(IReadOnlyList<SubtitleTrack> tracks, int episodeCount)
    {
        List<SubtitleLanguage> missing = MissingLanguages(tracks);
        return new SubtitleStatus(
            tracks,
            missing,
            episodeCount,
            missing.Count > 0 ? episodeCount : 0);
    }

    List<SubtitleLanguage> MissingLanguages(IReadOnlyList<SubtitleTrack> tracks)
    {
        IReadOnlyList<string> configured = SubtitleLanguages.NormalizeTwoLetter(config.CurrentValue.Languages);
        return [.. configured
            .Where(language => !tracks.Any(track => SubtitleLanguages.Matches(track.LanguageCode, language)))
            .Select(language => new SubtitleLanguage(language, SubtitleLanguages.DisplayName(language)))];
    }
}
