using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Navidrome.Playback;
using Stackarr.TestSupport;

namespace Stackarr.Navidrome.Tests;

/// <summary>
/// Covers the lyrics proxy's two halves: the Subsonic client reading Navidrome's OpenSubsonic
/// <c>getLyricsBySongId</c> payload, and the mapper flattening it into what the player renders.
/// </summary>
public class NavidromeLyricsTests
{
    static NavidromeClient Client(FakeHttpMessageHandler handler) =>
        new("http://navidrome:4533/", "u", "p", handler);

    const string EnhancedPayload = """
        {
          "subsonic-response": {
            "status": "ok",
            "version": "1.16.1",
            "lyricsList": {
              "structuredLyrics": [
                {
                  "kind": "main",
                  "lang": "eng",
                  "synced": true,
                  "line": [
                    { "start": 41160, "value": "It's bugging me" },
                    { "start": 43620, "value": "Grating me" }
                  ],
                  "cueLine": [
                    {
                      "index": 0,
                      "start": 41160,
                      "end": 43620,
                      "value": "It's bugging me",
                      "cue": [
                        { "start": 41160, "end": 42200, "value": "It's", "byteStart": 0, "byteEnd": 3 },
                        { "start": 42200, "end": 43620, "value": "bugging me", "byteStart": 5, "byteEnd": 14 }
                      ]
                    }
                  ]
                },
                {
                  "kind": "translation",
                  "lang": "deu",
                  "synced": true,
                  "line": [ { "start": 41160, "value": "Es nervt mich" } ]
                }
              ]
            }
          }
        }
        """;

    [Test]
    public async Task Client_ReadsStructuredLyrics()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson(EnhancedPayload);

        List<SubsonicStructuredLyrics> tracks = await Client(handler).GetLyricsBySongIdAsync("song-1");

        await Assert.That(tracks.Count).IsEqualTo(2);
        await Assert.That(tracks[0].Kind).IsEqualTo("main");
        await Assert.That(tracks[0].Line[0].Start).IsEqualTo(41160);
        await Assert.That(tracks[0].CueLine[0].Cue.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[^1].Uri.Query).Contains("enhanced=true");
    }

    [Test]
    public async Task Client_ReturnsEmptyWhenNavidromeHasNoLyrics()
    {
        // Navidrome answers a missing lyrics record with a "failed" body, not an HTTP error.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson(
            """{"subsonic-response":{"status":"failed","error":{"code":70,"message":"data not found"}}}""");

        List<SubsonicStructuredLyrics> tracks = await Client(handler).GetLyricsBySongIdAsync("song-1");

        await Assert.That(tracks.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Mapper_FlattensTheMainTrackForThePlayer()
    {
        List<SubsonicStructuredLyrics> tracks = await Client(
            new FakeHttpMessageHandler().EnqueueJson(EnhancedPayload)).GetLyricsBySongIdAsync("song-1");

        LyricsResponse? response = MusicLyricsMapper.ToResponse("song-1", tracks);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.SongId).IsEqualTo("song-1");
        await Assert.That(response.Main.Synced).IsTrue();
        await Assert.That(response.Main.Lines.Count).IsEqualTo(2);
        await Assert.That(response.Main.Lines[0].Text).IsEqualTo("It's bugging me");
        // Word cues are flattened with the line they belong to.
        await Assert.That(response.Main.Cues.Count).IsEqualTo(2);
        await Assert.That(response.Main.Cues[0].LineIndex).IsEqualTo(0);
        await Assert.That(response.Main.Cues[1].Start).IsEqualTo(42200);
    }

    [Test]
    public async Task Mapper_PrefersASyncedMainTrackOverAPlainOne()
    {
        SubsonicStructuredLyrics plain = new() { Kind = "main", Synced = false, Line = [new SubsonicLyricLine { Value = "plain" }] };
        SubsonicStructuredLyrics synced = new() { Kind = "main", Synced = true, Line = [new SubsonicLyricLine { Start = 1000, Value = "synced" }] };

        LyricsResponse? response = MusicLyricsMapper.ToResponse("s", [plain, synced]);

        await Assert.That(response!.Main.Synced).IsTrue();
        await Assert.That(response.Main.Lines[0].Text).IsEqualTo("synced");
    }

    [Test]
    public async Task Mapper_TreatsAMissingKindAsMain()
    {
        // Without enhanced=true Navidrome omits `kind`; the record is still the main lyrics.
        SubsonicStructuredLyrics track = new() { Line = [new SubsonicLyricLine { Value = "hello" }] };

        LyricsResponse? response = MusicLyricsMapper.ToResponse("s", [track]);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.Main.Kind).IsEqualTo("main");
    }

    [Test]
    public async Task Mapper_ReturnsNullWhenThereAreNoLyricsOrNoLines()
    {
        await Assert.That(MusicLyricsMapper.ToResponse("s", [])).IsNull();
        await Assert.That(MusicLyricsMapper.ToResponse("s", [new SubsonicStructuredLyrics { Kind = "main" }])).IsNull();
    }

    [Test]
    public async Task Mapper_FillsMissingCueBoundsFromTheCueLine()
    {
        SubsonicStructuredLyrics track = new()
        {
            Kind = "main",
            Synced = true,
            Line = [new SubsonicLyricLine { Start = 0, Value = "oh love" }],
            CueLine =
            [
                new SubsonicCueLine
                {
                    Index = 0, Start = 0, End = 2400, Value = "oh love",
                    Cue = [new SubsonicLyricCue { Value = "oh" }, new SubsonicLyricCue { Start = 900, Value = "love" }]
                }
            ]
        };

        LyricsResponse response = MusicLyricsMapper.ToResponse("s", [track])!;

        await Assert.That(response.Main.Cues[0].Start).IsEqualTo(0);
        await Assert.That(response.Main.Cues[0].End).IsEqualTo(2400);   // inherited from the cueLine
        await Assert.That(response.Main.Cues[1].Start).IsEqualTo(900);
        await Assert.That(response.Main.Cues[1].End).IsEqualTo(2400);
    }

    // Service wrapper

    static NavidromeService Service(FakeHttpMessageHandler handler) => new(
        Client(handler),
        new NavidromeApiSecret { Username = "stackarr", Password = "secret" },
        new MutableOptionsMonitor<NavidromeConfig>(new NavidromeConfig { Enabled = true }),
        NullLogger<NavidromeService>.Instance);

    [Test]
    public async Task Service_ReturnsLyricsAndCachesTheHit()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson(EnhancedPayload);
        NavidromeService service = Service(handler);

        List<SubsonicStructuredLyrics> first = await service.GetLyricsAsync("song-1");
        List<SubsonicStructuredLyrics> second = await service.GetLyricsAsync("song-1");

        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That(second.Count).IsEqualTo(2);
        await Assert.That(handler.Requests.Count).IsEqualTo(1);   // the second read came from the cache
    }

    [Test]
    public async Task Service_DoesNotCacheAMiss()
    {
        // A miss must be re-checked, or lyrics written later would stay invisible for the cache's life.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
                """{"subsonic-response":{"status":"failed","error":{"code":70,"message":"data not found"}}}"""))
        };
        NavidromeService service = Service(handler);

        await service.GetLyricsAsync("song-1");
        await service.GetLyricsAsync("song-1");

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Service_DegradesToNoLyricsWhenNavidromeFails()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("{}", HttpStatusCode.InternalServerError))
        };

        List<SubsonicStructuredLyrics> tracks = await Service(handler).GetLyricsAsync("song-1");

        await Assert.That(tracks.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Service_IsInertWhenNavidromeIsDisabled()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler();
        NavidromeService service = new(
            Client(handler),
            new NavidromeApiSecret { Username = "stackarr", Password = "secret" },
            new MutableOptionsMonitor<NavidromeConfig>(new NavidromeConfig { Enabled = false }),
            NullLogger<NavidromeService>.Instance);

        await Assert.That((await service.GetLyricsAsync("song-1")).Count).IsEqualTo(0);
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }
}
