using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Playback;
using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// Continue Watching against a real Jellyfin container. These pin the streaming-service rules the
/// raw Jellyfin endpoints do not apply themselves: one card per series, near-end items treated as
/// finished, and the next episode offered after an episode is finished.
/// </summary>
[NotInParallel(JellyfinServerShared.ParallelKey)]
public class ContinueWatchingIntegrationTests
{
    [Test]
    public async Task TwoInProgressEpisodes_CollapseToOneCard()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        await using WatchProbe probe = await WatchProbe.CreateAsync(server);
        long runtime = probe.Episode1.RunTimeTicks ?? 0;
        await probe.Admin.SetUserPositionAsync(probe.User.Id, probe.Episode1.Id, (long)(runtime * 0.3));
        await probe.Admin.SetUserPositionAsync(probe.User.Id, probe.Episode2.Id, (long)(runtime * 0.2));

        IReadOnlyList<ContinueWatchingItem> items = await probe.Provider.GetContinueWatchingAsync(probe.User.Name);

        await Assert.That(items.Count).IsEqualTo(1);
        await Assert.That(items[0].Key.EpisodeNumber).IsEqualTo(2);
        await Assert.That(items[0].IsUpNext).IsFalse();
    }

    [Test]
    public async Task NearEndEpisode_RecommendsTheNextEpisode()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        await using WatchProbe probe = await WatchProbe.CreateAsync(server);
        long runtime = probe.Episode1.RunTimeTicks ?? 0;
        await probe.Admin.SetUserPositionAsync(probe.User.Id, probe.Episode1.Id, runtime - 15 * TimeSpan.TicksPerSecond);

        JellyfinItemsResult rawResume = await probe.Admin.GetResumeItemsAsync(probe.User.Id, 20);
        await Assert.That(rawResume.Items.Any(item => item.Id == probe.Episode1.Id)).IsTrue();
        await Assert.That(probe.Episode1.SeriesId).IsNotEmpty();

        JellyfinItemsResult seriesEpisodes = await probe.Admin.GetSeriesEpisodesAsync(probe.Episode1.SeriesId!);
        await Assert.That(seriesEpisodes.Items.Any(item => item.Id == probe.Episode2.Id)).IsTrue();

        JellyfinItem? secondState = await probe.Admin.GetUserItemAsync(probe.User.Id, probe.Episode2.Id);
        await Assert.That(secondState!.UserData!.Played).IsFalse();

        IReadOnlyList<ContinueWatchingItem> items = await probe.Provider.GetContinueWatchingAsync(probe.User.Name);

        await Assert.That(items.Count).IsEqualTo(1);
        await Assert.That(items[0].Key.EpisodeNumber).IsEqualTo(2);
        await Assert.That(items[0].IsUpNext).IsTrue();
        await Assert.That(items[0].PositionTicks).IsEqualTo(0);
        await Assert.That(items[0].Progress).IsEqualTo(0);
    }

    [Test]
    public async Task NearEndMovie_IsTreatedAsFinished()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        await using WatchProbe probe = await WatchProbe.CreateAsync(server);
        long runtime = probe.Movie.RunTimeTicks ?? 0;
        await probe.Admin.SetUserPositionAsync(probe.User.Id, probe.Movie.Id, runtime - 30 * TimeSpan.TicksPerSecond);

        IReadOnlyList<ContinueWatchingItem> items = await probe.Provider.GetContinueWatchingAsync(probe.User.Name);

        await Assert.That(items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InProgressMovie_IsStillOffered()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        await using WatchProbe probe = await WatchProbe.CreateAsync(server);
        long runtime = probe.Movie.RunTimeTicks ?? 0;
        await probe.Admin.SetUserPositionAsync(probe.User.Id, probe.Movie.Id, (long)(runtime * 0.4));

        IReadOnlyList<ContinueWatchingItem> items = await probe.Provider.GetContinueWatchingAsync(probe.User.Name);

        await Assert.That(items.Count).IsEqualTo(1);
        await Assert.That(items[0].IsEpisode).IsFalse();
        await Assert.That(items[0].Progress).IsGreaterThan(0.3);
    }

    [Test]
    public async Task FinishedEpisode_OffersTheNextEpisodeFromNextUp()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        await using WatchProbe probe = await WatchProbe.CreateAsync(server);
        await probe.Admin.SetPlayedAsync(probe.User.Id, probe.Episode1.Id, played: true);

        IReadOnlyList<ContinueWatchingItem> items = await probe.Provider.GetContinueWatchingAsync(probe.User.Name);

        await Assert.That(items.Count).IsEqualTo(1);
        await Assert.That(items[0].Key.EpisodeNumber).IsEqualTo(2);
        await Assert.That(items[0].IsUpNext).IsTrue();
    }

    [Test]
    public async Task FinishedLastEpisode_LeavesNoCard()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        await using WatchProbe probe = await WatchProbe.CreateAsync(server);
        await probe.Admin.SetPlayedAsync(probe.User.Id, probe.Episode1.Id, played: true);
        await probe.Admin.SetPlayedAsync(probe.User.Id, probe.Episode2.Id, played: true);
        await probe.Admin.SetPlayedAsync(probe.User.Id, probe.Episode3.Id, played: true);

        IReadOnlyList<ContinueWatchingItem> items = await probe.Provider.GetContinueWatchingAsync(probe.User.Name);

        await Assert.That(items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OlderInProgressEpisode_YieldsToTheNextUpAfterALaterPlayedEpisode()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        await using WatchProbe probe = await WatchProbe.CreateAsync(server);
        long runtime = probe.Episode1.RunTimeTicks ?? 0;
        await probe.Admin.SetUserPositionAsync(probe.User.Id, probe.Episode1.Id, (long)(runtime * 0.3));
        await probe.Admin.SetPlayedAsync(probe.User.Id, probe.Episode2.Id, played: true);

        IReadOnlyList<ContinueWatchingItem> items = await probe.Provider.GetContinueWatchingAsync(probe.User.Name);

        await Assert.That(items.Count).IsEqualTo(1);
        await Assert.That(items[0].Key.EpisodeNumber).IsEqualTo(3);
        await Assert.That(items[0].IsUpNext).IsTrue();
    }

    /// <summary>
    /// A throwaway Jellyfin user, its library items and a provider bound to the admin token. Every
    /// test gets fresh watch state, so test order cannot influence the result.
    /// </summary>
    sealed class WatchProbe : IAsyncDisposable
    {
        public required JellyfinClient Admin { get; init; }
        public required JellyfinUser User { get; init; }
        public required JellyfinWatchStateProvider Provider { get; init; }
        public required JellyfinItem Episode1 { get; init; }
        public required JellyfinItem Episode2 { get; init; }
        public required JellyfinItem Episode3 { get; init; }
        public required JellyfinItem Movie { get; init; }

        public static async Task<WatchProbe> CreateAsync(JellyfinServer server)
        {
            JellyfinClient admin = server.CreateClient();
            JellyfinUser user = await admin.CreateUserAsync($"cw{Guid.NewGuid():N}"[..16], "");

            JellyfinItemsResult seriesItems = await server.WaitForItemsAsync("Series", TimeSpan.FromMinutes(3));
            JellyfinItem series = seriesItems.Items[0];
            List<JellyfinItem> ordered = await WaitForEpisodesAsync(admin, series.Id);

            JellyfinItemsResult movies = await server.WaitForItemsAsync("Movie", TimeSpan.FromMinutes(3));
            JellyfinItem? movie = await WaitForMovieAsync(admin, movies.Items[0].Id);

            JellyfinConfig config = new();
            JellyfinWatchStateProvider provider = new(
                admin,
                server.CreateLinkService(config),
                new MutableOptionsMonitor<JellyfinConfig>(config),
                NullLogger<JellyfinWatchStateProvider>.Instance);

            return new WatchProbe
            {
                Admin = admin,
                User = user,
                Provider = provider,
                Episode1 = ordered[0],
                Episode2 = ordered[1],
                Episode3 = ordered[2],
                Movie = movie
            };
        }

        /// <summary>
        /// Waits until two episodes have a real runtime. A freshly created library can list the
        /// series before ffprobe has filled in the episode durations, and the tail-window tests
        /// need that runtime to place a position near the end.
        /// </summary>
        static async Task<List<JellyfinItem>> WaitForEpisodesAsync(JellyfinClient admin, string seriesId)
        {
            Stopwatch sw = Stopwatch.StartNew();
            List<JellyfinItem> ordered = [];

            while (sw.Elapsed < TimeSpan.FromMinutes(3))
            {
                JellyfinItemsResult episodes = await admin.GetSeriesEpisodesAsync(seriesId);
                ordered = episodes.Items
                    .Where(e => e.ParentIndexNumber is > 0 && e.IndexNumber is > 0)
                    .OrderBy(e => e.ParentIndexNumber)
                    .ThenBy(e => e.IndexNumber)
                    .Take(3)
                    .ToList();

                if (ordered.Count == 3 && ordered.All(e => e.RunTimeTicks is > 0))
                    return ordered;

                await Task.Delay(1000);
            }

            throw new InvalidOperationException("The Jellyfin fixture did not expose two analysed episodes.");
        }

        static async Task<JellyfinItem> WaitForMovieAsync(JellyfinClient admin, string movieId)
        {
            Stopwatch sw = Stopwatch.StartNew();

            while (sw.Elapsed < TimeSpan.FromMinutes(3))
            {
                JellyfinItem? movie = await admin.GetItemAsync(movieId);
                if (movie?.RunTimeTicks is > 0)
                    return movie;

                await Task.Delay(1000);
            }

            throw new InvalidOperationException("The Jellyfin fixture did not analyse the movie.");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Jellyfin batches user-data notifications on a timer. Deleting a user while a
                // notification for them is still queued crashes the server (Invalid user ID in
                // UserDataChangeNotifier), so give the timer time to flush first.
                await Task.Delay(TimeSpan.FromSeconds(3));
                await Admin.DeleteUserAsync(User.Id);
            }
            catch (HttpRequestException)
            {
                /* the fixture tears the whole server down at the end of the session */
            }
        }
    }
}
