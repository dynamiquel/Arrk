using Arrk.Tv.Abstractions;

namespace Arrk.Tvarr.Services;

public partial class SonarrService
{
    async Task<IReadOnlyList<TvSeries>> ISeriesLibrary.GetAllSeriesAsync(CancellationToken ct)
        => await GetAllSeries(ct);

    Task<TvSeries?> ISeriesLibrary.GetSeriesAsync(int id, CancellationToken ct)
        => GetSeries(id, ct);

    async Task<IReadOnlyList<TvSeason>> ISeriesLibrary.GetSeasonsAsync(int seriesId, CancellationToken ct)
        => await GetSeasons(seriesId, ct);

    async Task<IReadOnlyList<TvEpisode>> ISeriesLibrary.GetEpisodesAsync(int seriesId, CancellationToken ct)
        => await GetEpisodes(seriesId, ct);

    Task ISeriesLibraryWriter.DeleteSeriesAsync(int id, bool deleteFiles, CancellationToken ct)
        => DeleteSeries(id, deleteFiles, ct);

    Task ISeriesLibraryWriter.DeleteEpisodeAsync(int episodeFileId, CancellationToken ct)
        => DeleteEpisode(episodeFileId, ct);

    Task<int> ISeriesLibraryWriter.AddSeriesWithOptionsAsync(
        int tvdbId,
        string title,
        int qualityProfileId,
        IReadOnlyCollection<int>? monitoredSeasons,
        bool monitorFutureSeasons,
        bool searchForSeries,
        CancellationToken ct)
        => AddSeriesWithOptions(
            tvdbId,
            title,
            qualityProfileId,
            monitoredSeasons?.ToHashSet(),
            monitorFutureSeasons,
            searchForSeries,
            ct);

    Task ISeriesLibraryWriter.ApplySeasonMonitoringAsync(
        int seriesId,
        IReadOnlyCollection<int> seasons,
        bool monitorFutureSeasons,
        CancellationToken ct)
        => ApplySeasonMonitoring(seriesId, seasons.ToHashSet(), monitorFutureSeasons, ct);

    Task ISeriesLibraryWriter.ToggleSeasonMonitoredAsync(
        int seriesId, int seasonNumber, bool monitored, CancellationToken ct)
        => ToggleSeasonMonitored(seriesId, seasonNumber, monitored, ct);

    Task ISeriesLibraryWriter.ToggleEpisodeMonitoredAsync(int episodeId, bool monitored, CancellationToken ct)
        => ToggleEpisodeMonitored(episodeId, monitored, ct);

    Task ISeriesLibraryWriter.CommandSearchSeriesAsync(int seriesId, CancellationToken ct)
        => CommandSearchSeries(seriesId, ct);

    Task ISeriesLibraryWriter.CommandSearchSeasonAsync(int seriesId, int seasonNumber, CancellationToken ct)
        => CommandSearchSeason(seriesId, seasonNumber, ct);

    Task ISeriesLibraryWriter.CommandSearchEpisodeAsync(int episodeId, CancellationToken ct)
        => CommandSearchEpisode(episodeId, ct);

    Task ISeriesLibraryWriter.SearchMissingEpisodesAsync(CancellationToken ct)
        => SearchMissingAsync(ct);
}
