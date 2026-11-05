using Stackarr.Tvarr.Components.Shared;
using Stackarr.Ui.Components;
using Stackarr.Tvarr.Services;
using Stackarr.Core.Enums;
using Stackarr.Core.Playback;

namespace Stackarr.Tvarr.Tests;

public class WatchStateEnricherTests
{
    sealed class StubWatchProvider : IWatchStateProvider
    {
        public string Name => "Stub";
        public bool IsAvailable { get; set; } = true;
        public Dictionary<int, WatchState> Movies { get; } = [];
        public Dictionary<int, SeriesWatchState> Series { get; } = [];

        public Task<IReadOnlyDictionary<MediaKey, WatchState>> GetStatesAsync(string username, IReadOnlyCollection<MediaKey> keys, CancellationToken ct = default)
        {
            Dictionary<MediaKey, WatchState> result = [];
            foreach (MediaKey key in keys)
                if (key.TmdbId > 0 && Movies.TryGetValue(key.TmdbId, out WatchState? state))
                    result[key] = state;
            return Task.FromResult<IReadOnlyDictionary<MediaKey, WatchState>>(result);
        }

        public Task<IReadOnlyDictionary<int, SeriesWatchState>> GetSeriesStatesAsync(string username, IReadOnlyCollection<int> ids, CancellationToken ct = default)
        {
            Dictionary<int, SeriesWatchState> result = [];
            foreach (int id in ids)
                if (Series.TryGetValue(id, out SeriesWatchState? state))
                    result[id] = state;
            return Task.FromResult<IReadOnlyDictionary<int, SeriesWatchState>>(result);
        }

        public Task<IReadOnlyList<ContinueWatchingItem>> GetContinueWatchingAsync(string username, int limit = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContinueWatchingItem>>([]);

        public Task<IReadOnlyList<WatchHistoryEntry>> GetHistoryAsync(string username, int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WatchHistoryEntry>>([]);
    }

    static MediaItem Movie(int tmdb) => new() { Title = "M", MediaType = "movie", TmdbId = tmdb };
    static MediaItem Show(int tvdb, int tmdb) => new() { Title = "S", MediaType = "tv", TvdbId = tvdb, TmdbId = tmdb };

    [Test]
    public async Task Movie_progress_and_watched_are_projected()
    {
        StubWatchProvider provider = new();
        provider.Movies[1] = new WatchState(false, 40, 1000, DateTime.UtcNow, 1);
        provider.Movies[2] = new WatchState(true, 100, 0, DateTime.UtcNow, 1);
        WatchStateEnricher enricher = new(new WatchStateService([provider]));

        List<MediaItem> result = await enricher.EnrichAsync("alice", [Movie(1), Movie(2)]);

        await Assert.That(result[0].WatchProgress).IsEqualTo(0.4);
        await Assert.That(result[0].IsWatched).IsFalse();
        await Assert.That(result[1].IsWatched).IsTrue();
        await Assert.That(result[1].WatchProgress).IsEqualTo(1);
    }

    [Test]
    public async Task Series_uses_watched_over_total()
    {
        StubWatchProvider provider = new();
        provider.Series[10] = new SeriesWatchState(3, 6, null, null);
        provider.Series[11] = new SeriesWatchState(8, 8, null, null);
        WatchStateEnricher enricher = new(new WatchStateService([provider]));

        List<MediaItem> result = await enricher.EnrichAsync("alice", [Show(10, 0), Show(11, 0)]);

        await Assert.That(result[0].WatchProgress).IsEqualTo(0.5);
        await Assert.That(result[0].IsWatched).IsFalse();
        await Assert.That(result[1].IsWatched).IsTrue();
    }

    [Test]
    public async Task No_provider_leaves_items_untouched()
    {
        StubWatchProvider provider = new() { IsAvailable = false };
        WatchStateEnricher enricher = new(new WatchStateService([provider]));

        List<MediaItem> result = await enricher.EnrichAsync("alice", [Movie(1)]);

        await Assert.That(result[0].HasWatchProgress).IsFalse();
    }
}