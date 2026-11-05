using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Configuration;
using Stackarr.Core.Playback;
using Stackarr.Jellyfin.Playback;
using Stackarr.TestSupport;
using System.Text;

namespace Stackarr.Jellyfin.Tests;

public class JellyfinDeviceProfileFactoryTests
{
    [Test]
    public async Task Build_NeverAdvertisesMkvDirectPlay()
    {
        JellyfinDeviceProfile profile = JellyfinDeviceProfileFactory.Build(new BrowserCapabilities { Hevc = true });

        await Assert.That(profile.DirectPlayProfiles.Any(p => p.Container == "mkv")).IsFalse();
        await Assert.That(profile.DirectPlayProfiles.Any(p => p.Container == "mp4")).IsTrue();
    }

    [Test]
    public async Task Build_WhenHevcSupported_TranscodeProfileOffersHevcCopy()
    {
        JellyfinDeviceProfile profile = JellyfinDeviceProfileFactory.Build(new BrowserCapabilities { Hevc = true });

        JellyfinTranscodingProfile video = profile.TranscodingProfiles.Single(p => p.Type == "Video");
        await Assert.That(video.Protocol).IsEqualTo("hls");
        await Assert.That(video.Container).IsEqualTo("mp4");
        await Assert.That(video.VideoCodec).Contains("hevc");
        await Assert.That(video.AudioCodec).IsEqualTo("aac");
    }

    [Test]
    public async Task Build_WhenHevcUnsupported_DoesNotOfferHevc()
    {
        JellyfinDeviceProfile profile = JellyfinDeviceProfileFactory.Build(new BrowserCapabilities { Hevc = false });

        JellyfinTranscodingProfile video = profile.TranscodingProfiles.Single(p => p.Type == "Video");
        await Assert.That(video.VideoCodec).DoesNotContain("hevc");
        await Assert.That(video.VideoCodec).Contains("h264");
    }

    [Test]
    public async Task Build_OnlySideLoadsTextSubtitles()
    {
        JellyfinDeviceProfile profile = JellyfinDeviceProfileFactory.Build(new BrowserCapabilities());

        await Assert.That(profile.SubtitleProfiles.Any(p => p.Format == "vtt" && p.Method == "External")).IsTrue();
        await Assert.That(profile.SubtitleProfiles.Any(p => p.Format == "ass" && p.Method == "External")).IsTrue();
        await Assert.That(profile.SubtitleProfiles.Any(p => p.Format == "pgs")).IsFalse();
        await Assert.That(profile.SubtitleProfiles.All(p => p.Method == "External")).IsTrue();
    }
}

public class HlsPlaylistRewriterTests
{
    const string Playlist =
        "#EXTM3U\n" +
        "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?DeviceId=d&MediaSourceId=m&ApiKey=SECRET&Tag=t\"\n" +
        "#EXTINF:11.6,\n" +
        "hls1/main/0.mp4?DeviceId=d&MediaSourceId=m&ApiKey=SECRET&runtimeTicks=0\n";

    [Test]
    public async Task StripAuthToken_RemovesApiKeyEverywhere()
    {
        string result = HlsPlaylistRewriter.StripAuthToken(Playlist);

        await Assert.That(result).DoesNotContain("SECRET");
        await Assert.That(result).DoesNotContain("ApiKey");
    }

    [Test]
    public async Task StripAuthToken_KeepsNonAuthParameters()
    {
        string result = HlsPlaylistRewriter.StripAuthToken(Playlist);

        await Assert.That(result).Contains("DeviceId=d");
        await Assert.That(result).Contains("MediaSourceId=m");
        await Assert.That(result).Contains("hls1/main/0.mp4");
    }

    [Test]
    public async Task StripAuthToken_HandlesApiKeyAsFirstParameter()
    {
        string result = HlsPlaylistRewriter.StripAuthToken("main.m3u8?ApiKey=SECRET&DeviceId=d");

        await Assert.That(result).IsEqualTo("main.m3u8?DeviceId=d");
    }

    [Test]
    public async Task StripAuthToken_HandlesApiKeyAsOnlyParameter()
    {
        string result = HlsPlaylistRewriter.StripAuthToken("main.m3u8?api_key=SECRET");

        await Assert.That(result).IsEqualTo("main.m3u8");
    }

    [Test]
    public async Task IsPlaylist_DetectsByPathAndContentType()
    {
        await Assert.That(HlsPlaylistRewriter.IsPlaylist(null, "videos/1/master.m3u8")).IsTrue();
        await Assert.That(HlsPlaylistRewriter.IsPlaylist("application/vnd.apple.mpegurl", null)).IsTrue();
        await Assert.That(HlsPlaylistRewriter.IsPlaylist("video/mp4", "hls1/main/0.mp4")).IsFalse();
    }
}

public class PlaybackSessionStoreTests
{
    [Test]
    public async Task GetOwned_ReturnsSessionOnlyForOwner()
    {
        PlaybackSessionStore store = new();
        PlaybackSession session = store.Create("alice", "item1", "ms1", "ps1");

        await Assert.That(store.GetOwned(session.SessionId, "alice")).IsNotNull();
        await Assert.That(store.GetOwned(session.SessionId, "bob")).IsNull();
    }

    [Test]
    public async Task Remove_DropsSession()
    {
        PlaybackSessionStore store = new();
        PlaybackSession session = store.Create("alice", "item1", "ms1", "ps1");

        store.Remove(session.SessionId);

        await Assert.That(store.Get(session.SessionId)).IsNull();
    }

    [Test]
    public async Task ReapIdle_RemovesOnlyStaleSessions()
    {
        PlaybackSessionStore store = new();
        PlaybackSession stale = store.Create("alice", "item1", "ms1", "ps1");
        PlaybackSession fresh = store.Create("bob", "item2", "ms2", "ps2");
        stale.LastSeenAt = DateTime.UtcNow - PlaybackSessionStore.IdleTimeout - TimeSpan.FromMinutes(1);

        IReadOnlyList<PlaybackSession> reaped = store.ReapIdle();

        await Assert.That(reaped.Select(s => s.SessionId)).Contains(stale.SessionId);
        await Assert.That(store.Get(stale.SessionId)).IsNull();
        await Assert.That(store.Get(fresh.SessionId)).IsNotNull();
    }
}

/// <summary>
/// Covers the web player's negotiation rules: the media source must be sent (otherwise Jellyfin
/// ignores explicit stream indexes), text subtitles must never be burned in, and image subtitles
/// must be, since a browser cannot render PGS as a &lt;track&gt;.
/// </summary>
public class JellyfinPlaybackTrackSelectionTests
{
    const string MediaStreams = """
        [
          { "Index": 0, "Type": "Video", "Codec": "h264" },
          { "Index": 1, "Type": "Audio", "Codec": "aac", "Language": "eng", "DisplayTitle": "English AAC", "IsDefault": true },
          { "Index": 2, "Type": "Audio", "Codec": "opus", "Language": "jpn", "DisplayTitle": "Japanese Opus", "Channels": 2 },
          { "Index": 4, "Type": "Subtitle", "Codec": "ass", "Language": "eng", "DisplayTitle": "English ASS", "IsDefault": true },
          { "Index": 5, "Type": "Subtitle", "Codec": "pgs", "Language": "eng", "DisplayTitle": "English PGS" }
        ]
        """;

    static string ItemJson => $$"""
        {
          "Id": "item1", "Name": "Episode", "Type": "Episode",
          "MediaSources": [ { "Id": "ms1", "RunTimeTicks": 3000000000, "MediaStreams": {{MediaStreams}} } ]
        }
        """;

    static string PlaybackJson => $$"""
        {
          "PlaySessionId": "ps1",
          "MediaSources": [ {
            "Id": "ms1", "RunTimeTicks": 3000000000,
            "TranscodingUrl": "/videos/item1/master.m3u8?MediaSourceId=ms1&ApiKey=secret",
            "MediaStreams": {{MediaStreams}}
          } ]
        }
        """;

    static FakeHttpMessageHandler Handler()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""[{ "Name": "alice", "Id": "u1" }]""");
        handler.EnqueueJson(ItemJson);
        handler.EnqueueJson(PlaybackJson);
        return handler;
    }

    static JellyfinPlaybackService CreateService(FakeHttpMessageHandler handler)
    {
        JellyfinClient client = new("http://test/jellyfin", "admin", "admin", "dev", handler);
        client.SetToken("token");
        return new JellyfinPlaybackService(
            client,
            new PlaybackSessionStore(),
            new StreamTokenService(Encoding.UTF8.GetBytes("0123456789abcdef")),
            new JellyfinLinkService(
                client,
                new MutableOptionsMonitor<JellyfinConfig>(new JellyfinConfig()),
                new MutableOptionsMonitor<RoutesConfig>(new RoutesConfig()),
                NullLogger<JellyfinLinkService>.Instance),
            new MutableOptionsMonitor<JellyfinConfig>(new JellyfinConfig()),
            NullLogger<JellyfinPlaybackService>.Instance);
    }

    static string PlaybackRequestBody(FakeHttpMessageHandler handler) =>
        handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)).Body;

    [Test]
    public async Task StartAsync_SendsSourceAndPicksDefaultTracks()
    {
        FakeHttpMessageHandler handler = Handler();

        PlaybackPlan? plan = await CreateService(handler).StartAsync(
            "alice", "item1", new BrowserCapabilities(), 100, audioStreamIndex: null, subtitleStreamIndex: null, default);

        await Assert.That(plan).IsNotNull();
        string body = PlaybackRequestBody(handler);
        await Assert.That(body).Contains("\"mediaSourceId\":\"ms1\"");
        await Assert.That(body).Contains("\"audioStreamIndex\":1");
        // A negative index tells Jellyfin not to burn the default text subtitle in.
        await Assert.That(body).Contains("\"subtitleStreamIndex\":-1");

        await Assert.That(plan!.AudioTracks.Count).IsEqualTo(2);
        await Assert.That(plan.SelectedAudioIndex).IsEqualTo(1);
        await Assert.That(plan.SelectedSubtitleIndex).IsEqualTo(4);
        await Assert.That(plan.SubtitlesBurnedIn).IsFalse();

        PlaybackSubtitle text = plan.Subtitles.Single(s => s.Index == 4);
        await Assert.That(text.IsText).IsTrue();
        await Assert.That(text.Url).Contains("/Videos/item1/ms1/Subtitles/4/Stream.vtt");
        await Assert.That(plan.Subtitles.Single(s => s.Index == 5).Url).IsNull();
    }

    [Test]
    public async Task StartAsync_ImageSubtitle_IsBurnedInAndHasNoSidecarUrl()
    {
        FakeHttpMessageHandler handler = Handler();

        PlaybackPlan? plan = await CreateService(handler).StartAsync(
            "alice", "item1", new BrowserCapabilities(), 100, audioStreamIndex: 2, subtitleStreamIndex: 5, default);

        await Assert.That(plan).IsNotNull();
        string body = PlaybackRequestBody(handler);
        await Assert.That(body).Contains("\"audioStreamIndex\":2");
        await Assert.That(body).Contains("\"subtitleStreamIndex\":5");

        await Assert.That(plan!.SelectedAudioIndex).IsEqualTo(2);
        await Assert.That(plan.SelectedSubtitleIndex).IsEqualTo(5);
        await Assert.That(plan.SubtitlesBurnedIn).IsTrue();
        await Assert.That(plan.Subtitles.Single(s => s.Index == 5).Url).IsNull();
    }

    [Test]
    public async Task StartAsync_SubtitlesOff_SendsMinusOneAndSelectsNothing()
    {
        FakeHttpMessageHandler handler = Handler();

        PlaybackPlan? plan = await CreateService(handler).StartAsync(
            "alice", "item1", new BrowserCapabilities(), 100, audioStreamIndex: 1, subtitleStreamIndex: -1, default);

        await Assert.That(plan).IsNotNull();
        await Assert.That(PlaybackRequestBody(handler)).Contains("\"subtitleStreamIndex\":-1");
        await Assert.That(plan!.SelectedSubtitleIndex).IsNull();
        await Assert.That(plan.SubtitlesBurnedIn).IsFalse();
    }
}

public class JellyfinPlaybackTitleTests
{
    [Test]
    public async Task BuildItemInfo_Episode_ShowsSeriesSeasonEpisodeAndTitle()
    {
        JellyfinItem episode = new()
        {
            Type = "Episode",
            Name = "Valar Dohaeris",
            SeriesName = "Game of Thrones",
            ParentIndexNumber = 3,
            IndexNumber = 1
        };

        PlaybackItemInfo info = JellyfinPlaybackService.BuildItemInfo(episode);

        await Assert.That(info.Title).IsEqualTo("Valar Dohaeris");
        await Assert.That(info.Subtitle).IsEqualTo("Game of Thrones · S03E01");
    }

    [Test]
    public async Task BuildItemInfo_EpisodeWithoutSeries_ShowsMarkerOnly()
    {
        JellyfinItem episode = new() { Type = "Episode", Name = "Pilot", ParentIndexNumber = 1, IndexNumber = 2 };

        PlaybackItemInfo info = JellyfinPlaybackService.BuildItemInfo(episode);

        await Assert.That(info.Title).IsEqualTo("Pilot");
        await Assert.That(info.Subtitle).IsEqualTo("S01E02");
    }

    [Test]
    public async Task BuildItemInfo_Movie_HasNoSubtitle()
    {
        JellyfinItem movie = new() { Type = "Movie", Name = "Matchbox the Movie" };

        PlaybackItemInfo info = JellyfinPlaybackService.BuildItemInfo(movie);

        await Assert.That(info.Title).IsEqualTo("Matchbox the Movie");
        await Assert.That(info.Subtitle).IsEqualTo("");
    }
}
