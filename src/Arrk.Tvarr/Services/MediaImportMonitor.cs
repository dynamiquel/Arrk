using Microsoft.Extensions.Logging;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Core.Playback;
using Arrk.Media.Abstractions;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Watches the arrs' history for completed imports and records a <c>downloads.imported</c>
/// notification the moment a movie or episode actually lands in the library, the point at which
/// the file becomes playable. There is no arr webhook to subscribe to, so history is polled: a
/// completed import appears as a <c>downloadFolderImported</c> record.
/// </summary>
/// <remarks>
/// The checkpoint (the last arr history id announced) is derived from the recorded events
/// themselves, each import event carries <c>{service}:{historyId}</c> as its source, so it
/// survives restarts without any extra state. When a service has no prior import event (a fresh
/// install, or an event log that was trimmed), the newest records are not backfilled: only imports
/// from the last couple of hours are announced, so nothing recent is missed without replaying the
/// whole library. A season pack imports every episode under one download id and is announced once.
/// </remarks>
public class MediaImportMonitor(
    RadarrService radarr,
    SonarrService sonarr,
    IEventStore events,
    IEventBus bus,
    ILogger<MediaImportMonitor> logger)
{
    /// <summary>Audit/notification topic recorded for every import; also the checkpoint query key.</summary>
    public const string ImportEventType = "downloads.imported";

    const string ArrImportEvent = "downloadFolderImported";
    const int HistoryPageSize = 200;
    const int CheckpointLimit = 500;

    /// <summary>On first start (no checkpoint) only imports this recent are announced, never the whole library.</summary>
    static readonly TimeSpan FirstRunWindow = TimeSpan.FromHours(2);

    public async Task PollAsync(CancellationToken ct = default)
    {
        IReadOnlyList<SystemEvent> prior = await events.QueryAsync(
            new SystemEventQuery { EventType = ImportEventType, Limit = CheckpointLimit }, ct);

        await PollRadarrAsync(MaxHistoryId(prior, "radarr"), ct);
        await PollSonarrAsync(MaxHistoryId(prior, "sonarr"), ct);
    }

    async Task PollRadarrAsync(int checkpoint, CancellationToken ct)
    {
        List<HistoryDto> history;
        try
        {
            history = await radarr.GetRecentHistory(HistoryPageSize, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Media import monitor could not read Radarr history this cycle");
            return;
        }

        if (history.Count == 0)
            return;

        foreach (HistoryDto record in SelectImports(history, checkpoint).OrderBy(h => h.Id))
        {
            await AnnounceMovieAsync(record, ct);
        }
    }

    async Task PollSonarrAsync(int checkpoint, CancellationToken ct)
    {
        List<HistoryDto> history;
        try
        {
            history = await sonarr.GetRecentHistory(HistoryPageSize, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Media import monitor could not read Sonarr history this cycle");
            return;
        }

        if (history.Count == 0)
            return;

        List<HistoryDto> imports = [.. SelectImports(history, checkpoint).OrderBy(h => h.Id)];

        // A season pack imports every episode in one go (same download id): announce it once.
        IEnumerable<IGrouping<string, HistoryDto>> groups = imports.GroupBy(
            h => string.IsNullOrWhiteSpace(h.DownloadId) ? $"history:{h.Id}" : h.DownloadId);

        foreach (IGrouping<string, HistoryDto> group in groups.OrderBy(g => g.Min(h => h.Id)))
        {
            await AnnounceEpisodesAsync([.. group], ct);
        }
    }

    async Task AnnounceMovieAsync(HistoryDto record, CancellationToken ct)
    {
        TvMovie? movie = record.MovieId is int movieId and > 0
            ? await TryGetMovieAsync(movieId, ct)
            : null;

        string display = Display(movie?.Title, record.SourceTitle, "A movie");
        string year = movie is { Year: > 0 } ? $" ({movie.Year})" : "";

        MediaEntityRef? entity = movie is { TmdbId: > 0 }
            ? new MediaEntityRef(
                "tv",
                TvMediaDomains.MovieDomain,
                MediaItemKeys.Movie(movie.TmdbId),
                movie.Title,
                movie.Year > 0 ? movie.Year.ToString() : null,
                movie.PosterUrl,
                movie.FanartUrl,
                Url: "/movies")
            : null;

        await AnnounceAsync(
            source: $"radarr:{record.Id}",
            title: "Movie imported",
            body: $"{display}{year} is now available in your library",
            sourceName: display,
            type: "Movie",
            poster: movie?.PosterUrl ?? "",
            fanart: movie?.FanartUrl ?? "",
            entity: entity,
            movieId: movie?.Id ?? record.MovieId,
            ct: ct);
    }

    async Task AnnounceEpisodesAsync(List<HistoryDto> group, CancellationToken ct)
    {
        int seriesId = group.Select(h => h.SeriesId).FirstOrDefault(id => id is > 0) ?? 0;
        TvSeries? series = seriesId > 0 ? await TryGetSeriesAsync(seriesId, ct) : null;

        string display = Display(series?.Title, group[0].SourceTitle, "A series");

        HashSet<int> episodeIds = [.. group
            .Select(h => h.EpisodeId)
            .Where(id => id is > 0)
            .Select(id => id!.Value)];

        List<TvEpisode> episodes = series is null ? [] : await TryGetEpisodesAsync(seriesId, ct);
        string range = series is null ? "" : EpisodeRange(episodes, episodeIds);

        string body = string.IsNullOrWhiteSpace(range)
            ? $"{display} has new episodes available in your library"
            : $"{display} {range} is now available in your library";

        MediaEntityRef? entity = BuildEpisodeEntity(series, episodes, episodeIds);

        await AnnounceAsync(
            source: $"sonarr:{group.Max(h => h.Id)}",
            title: "Episode imported",
            body: body,
            sourceName: display,
            type: "Episode",
            poster: series?.PosterUrl ?? "",
            fanart: series?.FanartUrl ?? "",
            entity: entity,
            seriesId: seriesId > 0 ? seriesId : null,
            episodeIds: episodeIds.Count > 0 ? [.. episodeIds] : null,
            ct: ct);
    }

    async Task AnnounceAsync(
        string source,
        string title,
        string body,
        string sourceName,
        string type,
        string poster,
        string fanart,
        MediaEntityRef? entity,
        CancellationToken ct,
        int? movieId = null,
        int? seriesId = null,
        IReadOnlyList<int>? episodeIds = null)
    {
        try
        {
            await events.RecordAsync(new SystemEventRecord
            {
                Category = EventCategory.Media,
                EventType = ImportEventType,
                Severity = EventSeverity.Success,
                Message = body,
                Source = source,
                SourceName = sourceName,
                Topic = DownloadNotificationTopics.DownloadImported,
                Notification = new NotificationMessage
                {
                    Key = DownloadNotificationTopics.DownloadImported,
                    Title = title,
                    Body = body,
                    Severity = EventSeverity.Success,
                    Source = source,
                    SourceName = sourceName,
                    Timestamp = DateTime.UtcNow,
                    Style = ArtStyle(poster, fanart),
                    Fields = [new("Type", type)],
                    Entity = entity,
                    Audience = entity is null
                        ? NotificationAudience.Broadcast
                        : NotificationAudience.Subscribers
                }
            }, ct);

            logger.LogInformation("Import notification recorded: {Body}", body);

            // A player may cache its library; tell it immediately rather than waiting for its
            // own poll interval. This is best-effort and must not fail the import record.
            await bus.Publish(new MediaImportedEvent(
                source,
                sourceName,
                type,
                MovieId: movieId,
                SeriesId: seriesId,
                EpisodeIds: episodeIds,
                Entity: entity), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to record import notification for {Source}", source);
        }
    }

    async Task<TvMovie?> TryGetMovieAsync(int id, CancellationToken ct)
    {
        try
        {
            return await radarr.GetMovie(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve movie {Id} for import notification", id);
            return null;
        }
    }

    async Task<TvSeries?> TryGetSeriesAsync(int id, CancellationToken ct)
    {
        try
        {
            return await sonarr.GetSeries(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve series {Id} for import notification", id);
            return null;
        }
    }

    async Task<List<TvEpisode>> TryGetEpisodesAsync(int seriesId, CancellationToken ct)
    {
        try
        {
            return await sonarr.GetEpisodes(seriesId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve episodes for series {Id} for import notification", seriesId);
            return [];
        }
    }

    /// <summary>
    /// Builds the entity subscribers match against. One episode uses the episode key; a
    /// same-season pack uses the season key; anything larger falls back to the series so a
    /// series follow still receives it.
    /// </summary>
    static MediaEntityRef? BuildEpisodeEntity(
        TvSeries? series,
        List<TvEpisode> episodes,
        HashSet<int> episodeIds)
    {
        if (series is null)
            return null;

        string seriesKey = MediaItemKeys.Series(series.TvdbId, series.TmdbId);
        if (string.IsNullOrEmpty(seriesKey))
            return null;

        List<TvEpisode> matched = [.. episodes
            .Where(e => episodeIds.Contains(e.Id))
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber)];

        if (matched.Count == 1)
        {
            TvEpisode episode = matched[0];
            string seasonKey = MediaItemKeys.Season(seriesKey, episode.SeasonNumber);
            return new MediaEntityRef(
                "tv",
                TvMediaDomains.TvDomain,
                MediaItemKeys.Episode(seriesKey, episode.SeasonNumber, episode.EpisodeNumber),
                series.Title,
                $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}",
                series.PosterUrl,
                series.FanartUrl,
                seasonKey,
                [seasonKey, seriesKey],
                Url: "/shows");
        }

        if (matched.Count > 1 && matched.Select(e => e.SeasonNumber).Distinct().Count() == 1)
        {
            int seasonNumber = matched[0].SeasonNumber;
            string seasonKey = MediaItemKeys.Season(seriesKey, seasonNumber);
            return new MediaEntityRef(
                "tv",
                TvMediaDomains.TvDomain,
                seasonKey,
                series.Title,
                $"Season {seasonNumber}",
                series.PosterUrl,
                series.FanartUrl,
                seriesKey,
                [seriesKey],
                Url: "/shows");
        }

        return new MediaEntityRef(
            "tv",
            TvMediaDomains.TvDomain,
            seriesKey,
            series.Title,
            null,
            series.PosterUrl,
            series.FanartUrl,
            Url: "/shows");
    }

    /// <summary>"S01E05" for one episode, "S01E05–E09" for a same-season pack, or a cross-season range.</summary>
    static string EpisodeRange(List<TvEpisode> episodes, HashSet<int> episodeIds)
    {
        List<TvEpisode> matched = [.. episodes
            .Where(e => episodeIds.Contains(e.Id))
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber)];

        if (matched.Count == 0)
            return "";

        TvEpisode first = matched[0];
        if (matched.Count == 1)
            return $"S{first.SeasonNumber:00}E{first.EpisodeNumber:00}";

        TvEpisode last = matched[^1];
        return first.SeasonNumber == last.SeasonNumber
            ? $"S{first.SeasonNumber:00}E{first.EpisodeNumber:00}–E{last.EpisodeNumber:00}"
            : $"S{first.SeasonNumber:00}E{first.EpisodeNumber:00}–S{last.SeasonNumber:00}E{last.EpisodeNumber:00}";
    }

    static string Display(string? preferred, string fallback, string placeholder)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;

        return string.IsNullOrWhiteSpace(fallback) ? placeholder : fallback;
    }

    static NotificationStyle? ArtStyle(string poster, string fanart)
    {
        if (string.IsNullOrWhiteSpace(poster) && string.IsNullOrWhiteSpace(fanart))
            return null;

        return new NotificationStyle { ThumbnailUrl = poster, ImageUrl = fanart };
    }

    /// <summary>
    /// The import records worth announcing: newer than the checkpoint, or, on first start, only
    /// those from the last <see cref="FirstRunWindow"/> so the library is not replayed.
    /// </summary>
    static IEnumerable<HistoryDto> SelectImports(List<HistoryDto> history, int checkpoint)
    {
        if (checkpoint >= 0)
            return history.Where(h => h.EventType == ArrImportEvent && h.Id > checkpoint);

        DateTime cutoff = DateTime.UtcNow - FirstRunWindow;
        return history.Where(h => h.EventType == ArrImportEvent && h.Date > cutoff);
    }

    /// <summary>The highest history id already announced for <paramref name="service"/>, or -1 when none.</summary>
    static int MaxHistoryId(IReadOnlyList<SystemEvent> prior, string service)
    {
        int max = -1;
        string prefix = service + ":";

        foreach (SystemEvent item in prior)
        {
            if (item.Source is null || !item.Source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (int.TryParse(item.Source[prefix.Length..], out int id) && id > max)
                max = id;
        }

        return max;
    }
}
