using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Events;
using Arrk.Media.Abstractions;
using Arrk.Tv.Abstractions;

namespace Arrk.Subtitles;

/// <summary>
/// Keeps new imports covered without a full-library sweep. The dispatcher chooses the provider
/// and candidate; this service only decides which targets are recent enough to check.
/// </summary>
public sealed class SubtitleAutoDownloader(
    SubtitleService subtitles,
    IOptionsMonitor<SubtitlesConfig> config,
    SubtitleLibraryResolver library,
    ILogger<SubtitleAutoDownloader> logger)
{
    static readonly TimeSpan CatchUpWindow = TimeSpan.FromDays(7);

    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task HandleImportAsync(MediaImportedEvent evt, CancellationToken ct)
    {
        if (!CanRun())
            return;

        await _gate.WaitAsync(ct);
        subtitles.BeginAutomaticRun();
        try
        {
            if (evt.MovieId is int movieId and > 0)
            {
                await subtitles.RequestAsync(SubtitleTarget.Movie(movieId), ct: ct);
                return;
            }

            if (evt.SeriesId is int seriesId and > 0 && evt.EpisodeIds is { Count: > 0 } episodeIds)
            {
                foreach (int episodeId in episodeIds)
                    await subtitles.RequestAsync(SubtitleTarget.Episode(seriesId, episodeId, 0), ct: ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not download subtitles for an imported item");
        }
        finally
        {
            subtitles.EndAutomaticRun();
            _gate.Release();
        }
    }

    public async Task CatchUpAsync(CancellationToken ct)
    {
        if (!CanRun())
            return;

        await _gate.WaitAsync(ct);
        subtitles.BeginAutomaticRun();
        try
        {
            DateTime cutoff = DateTime.UtcNow - CatchUpWindow;

            foreach (TvMovie movie in await library.GetAllMoviesAsync(ct))
            {
                if (ct.IsCancellationRequested)
                    return;

                if (!IsRecent(movie.File, cutoff))
                    continue;

                await subtitles.RequestAsync(SubtitleTarget.Movie(movie.Id, movie.Title), ct: ct);
            }

            foreach (TvSeries series in await library.GetAllSeriesAsync(ct))
            {
                if (ct.IsCancellationRequested)
                    return;

                if (series.Added < cutoff && (series.LastAired is null || series.LastAired < cutoff))
                    continue;

                List<TvEpisode> episodes = await library.GetEpisodesAsync(series.Id, ct);
                foreach (TvEpisode episode in episodes)
                {
                    if (!episode.HasFile || !IsRecent(episode.File, cutoff))
                        continue;

                    await subtitles.RequestAsync(
                        SubtitleTarget.Episode(
                            series.Id,
                            episode.Id,
                            episode.SeasonNumber,
                            episode.Title,
                            series.Title),
                        ct: ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not catch up on recent subtitle imports");
        }
        finally
        {
            subtitles.EndAutomaticRun();
            _gate.Release();
        }
    }

    bool CanRun() =>
        config.CurrentValue.Enabled
        && config.CurrentValue.AutoDownload
        && subtitles.IsAvailable;

    static bool IsRecent(MediaFileInfo? file, DateTime cutoff)
    {
        if (file is null)
            return false;

        DateTime added = file.DateAdded == default ? DateTime.MinValue : file.DateAdded;
        return added >= cutoff;
    }
}
