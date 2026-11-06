using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Playback;
using Arrk.Jellyfin.Playback;
using Arrk.TestSupport;

namespace Arrk.Jellyfin.Tests;

/// <summary>
/// Autoplay resolution against the real Jellyfin container: the following episode, the last
/// episode, and a movie all have to behave exactly as the overlay expects.
/// </summary>
[NotInParallel(JellyfinServerShared.ParallelKey)]
public class NextEpisodeIntegrationTests
{
    [Test]
    public async Task Episode_ResolvesTheFollowingEpisode()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        JellyfinClient client = server.CreateClient();
        List<JellyfinItem> episodes = await EpisodesAsync(server, client);
        JellyfinPlaybackService service = CreateService(server, client);

        PlaybackNextEpisode? next = await service.GetNextEpisodeAsync(episodes[0].Id, default);

        await Assert.That(next).IsNotNull();
        await Assert.That(next!.ItemId).IsEqualTo(episodes[1].Id);
        await Assert.That(next.SeasonNumber).IsEqualTo(episodes[1].ParentIndexNumber);
        await Assert.That(next.EpisodeNumber).IsEqualTo(episodes[1].IndexNumber);
        await Assert.That(next.ImageUrl).Contains(episodes[1].Id);
        await Assert.That(next.JellyfinUrl).Contains(episodes[1].Id);
    }

    [Test]
    public async Task LastEpisode_HasNoNext()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        JellyfinClient client = server.CreateClient();
        List<JellyfinItem> episodes = await EpisodesAsync(server, client);
        JellyfinPlaybackService service = CreateService(server, client);

        PlaybackNextEpisode? next = await service.GetNextEpisodeAsync(episodes[^1].Id, default);

        await Assert.That(next).IsNull();
    }

    [Test]
    public async Task Movie_HasNoNext()
    {
        JellyfinServer? server = await JellyfinServerShared.GetServerAsync();
        if (server is null || !server.HasMedia)
            return;

        JellyfinClient client = server.CreateClient();
        JellyfinItemsResult movies = await server.WaitForItemsAsync("Movie", TimeSpan.FromMinutes(3));
        JellyfinPlaybackService service = CreateService(server, client);

        PlaybackNextEpisode? next = await service.GetNextEpisodeAsync(movies.Items[0].Id, default);

        await Assert.That(next).IsNull();
    }

    static async Task<List<JellyfinItem>> EpisodesAsync(JellyfinServer server, JellyfinClient client)
    {
        JellyfinItemsResult seriesItems = await server.WaitForItemsAsync("Series", TimeSpan.FromMinutes(3));
        JellyfinItemsResult episodes = await client.GetSeriesEpisodesAsync(seriesItems.Items[0].Id);

        List<JellyfinItem> ordered = episodes.Items
            .Where(episode => episode.ParentIndexNumber is > 0 && episode.IndexNumber is > 0)
            .OrderBy(episode => episode.ParentIndexNumber)
            .ThenBy(episode => episode.IndexNumber)
            .ToList();

        if (ordered.Count < 2)
            throw new InvalidOperationException("The Jellyfin fixture needs at least two episodes.");

        return ordered;
    }

    static JellyfinPlaybackService CreateService(JellyfinServer server, JellyfinClient client)
    {
        JellyfinConfig config = new();
        return new JellyfinPlaybackService(
            client,
            new PlaybackSessionStore(),
            new StreamTokenService(Encoding.UTF8.GetBytes("0123456789abcdef")),
            server.CreateLinkService(config),
            new MutableOptionsMonitor<JellyfinConfig>(config),
            NullLogger<JellyfinPlaybackService>.Instance);
    }
}
