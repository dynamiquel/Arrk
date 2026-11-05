using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Configuration;
using Stackarr.Core.Playback;
using Stackarr.Jellyfin.Playback;
using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// Covers the autoplay hand-off: the next card must follow the series order (season, then episode),
/// skip specials, and never appear for a movie or the final episode.
/// </summary>
public class NextEpisodeTests
{
    const string Episode1 = """
        { "Id": "e1", "Name": "Episode One", "Type": "Episode", "SeriesId": "series1",
          "SeriesName": "Some Show", "ParentIndexNumber": 1, "IndexNumber": 1, "RunTimeTicks": 1500000000 }
        """;

    const string Episode2 = """
        { "Id": "e2", "Name": "Episode Two", "Type": "Episode", "SeriesId": "series1",
          "SeriesName": "Some Show", "ParentIndexNumber": 1, "IndexNumber": 2, "RunTimeTicks": 1500000000 }
        """;

    const string Special = """
        { "Id": "sp1", "Name": "Special", "Type": "Episode", "SeriesId": "series1",
          "SeriesName": "Some Show", "ParentIndexNumber": 0, "IndexNumber": 1, "RunTimeTicks": 600000000 }
        """;

    [Test]
    public async Task NextEpisode_FollowsSeasonAndEpisodeOrder()
    {
        FakeHttpMessageHandler handler = Handler(Episode1, Episodes(Special, Episode2, Episode1));
        JellyfinPlaybackService service = CreateService(handler);

        PlaybackNextEpisode? next = await service.GetNextEpisodeAsync("e1", default);

        await Assert.That(next).IsNotNull();
        await Assert.That(next!.ItemId).IsEqualTo("e2");
        await Assert.That(next.Title).IsEqualTo("Episode Two");
        await Assert.That(next.SeasonNumber).IsEqualTo(1);
        await Assert.That(next.EpisodeNumber).IsEqualTo(2);
        await Assert.That(next.Subtitle).IsEqualTo("Some Show · S01E02");
        await Assert.That(next.ImageUrl).Contains("/api/media/player/image/e2");
    }

    [Test]
    public async Task LastEpisode_HasNoNext()
    {
        FakeHttpMessageHandler handler = Handler(Episode1, Episodes(Episode1));
        JellyfinPlaybackService service = CreateService(handler);

        PlaybackNextEpisode? next = await service.GetNextEpisodeAsync("e1", default);

        await Assert.That(next).IsNull();
    }

    [Test]
    public async Task Movie_HasNoNext()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{ "Id": "m1", "Name": "A Movie", "Type": "Movie" }""");
        JellyfinPlaybackService service = CreateService(handler);

        PlaybackNextEpisode? next = await service.GetNextEpisodeAsync("m1", default);

        await Assert.That(next).IsNull();
    }

    [Test]
    public async Task EpisodeOutsideTheSeriesList_HasNoNext()
    {
        FakeHttpMessageHandler handler = Handler(Episode1, Episodes(Episode2));
        JellyfinPlaybackService service = CreateService(handler);

        PlaybackNextEpisode? next = await service.GetNextEpisodeAsync("e1", default);

        await Assert.That(next).IsNull();
    }

    static string Episodes(params string[] episodes) =>
        $$"""{ "Items": [{{string.Join(",", episodes)}}] }""";

    static FakeHttpMessageHandler Handler(string current, string episodes)
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(current);
        handler.EnqueueJson(episodes);
        return handler;
    }

    static JellyfinPlaybackService CreateService(FakeHttpMessageHandler handler)
    {
        JellyfinClient client = new("http://test/jellyfin", "admin", "admin", "dev", handler);
        client.SetToken("token");
        JellyfinConfig config = new();
        return new JellyfinPlaybackService(
            client,
            new PlaybackSessionStore(),
            new StreamTokenService(Encoding.UTF8.GetBytes("0123456789abcdef")),
            new JellyfinLinkService(
                client,
                new MutableOptionsMonitor<JellyfinConfig>(config),
                new MutableOptionsMonitor<RoutesConfig>(new RoutesConfig()),
                NullLogger<JellyfinLinkService>.Instance),
            new MutableOptionsMonitor<JellyfinConfig>(config),
            NullLogger<JellyfinPlaybackService>.Instance);
    }
}
