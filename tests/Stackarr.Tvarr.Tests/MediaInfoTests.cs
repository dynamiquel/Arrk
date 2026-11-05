using Stackarr.Core.Models;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

public class MediaFormatSummaryTests
{
    [Test]
    public async Task FriendlyAudio_MapsCommonFormatsAndChannels()
    {
        await Assert.That(MediaFormatSummary.FriendlyAudio("EAC3", 5.1)).IsEqualTo("Dolby Digital Plus 5.1");
        await Assert.That(MediaFormatSummary.FriendlyAudio("AC3", 6)).IsEqualTo("Dolby Digital 5.1");
        await Assert.That(MediaFormatSummary.FriendlyAudio("AAC", 2)).IsEqualTo("AAC 2.0");
        await Assert.That(MediaFormatSummary.FriendlyAudio("DTS-HD MA", 8, "DTS-HD MA")).IsEqualTo("DTS-HD MA 7.1");
    }

    [Test]
    public async Task FriendlyAudio_AtmosImpliesLayout()
    {
        await Assert.That(MediaFormatSummary.FriendlyAudio("TRUEHD", 8, "Dolby Atmos")).IsEqualTo("Dolby Atmos TrueHD");
        await Assert.That(MediaFormatSummary.FriendlyAudio("EAC3", 6, "Dolby Digital Plus + Dolby Atmos")).IsEqualTo("Dolby Atmos");
    }

    [Test]
    public async Task Hdr_MapsDynamicRangeCombinations()
    {
        (string text, MediaDynamicRange range) dv = MediaFormatSummary.Hdr("HDR", "DV HDR10Plus");
        await Assert.That(dv.text).IsEqualTo("Dolby Vision");
        await Assert.That(dv.range).IsEqualTo(MediaDynamicRange.DolbyVision);

        (string text, MediaDynamicRange _) hdr10 = MediaFormatSummary.Hdr("HDR", "HDR10");
        await Assert.That(hdr10.text).IsEqualTo("HDR");

        (string text, MediaDynamicRange _) plus = MediaFormatSummary.Hdr("HDR", "HDR10Plus");
        await Assert.That(plus.text).IsEqualTo("HDR10+");

        (string text, MediaDynamicRange _) sdr = MediaFormatSummary.Hdr("", "");
        await Assert.That(sdr.text).IsEqualTo("SDR");
    }

    [Test]
    public async Task ResolutionFromDimensions_PrefersWidthOverCroppedHeight()
    {
        await Assert.That(MediaFormatSummary.ResolutionFromDimensions(1920, 804)).IsEqualTo("1080p");
        await Assert.That(MediaFormatSummary.ResolutionFromDimensions(3840, 1600)).IsEqualTo("2160p");
        await Assert.That(MediaFormatSummary.ResolutionFromDimensions(1280, 536)).IsEqualTo("720p");
    }

    [Test]
    public async Task BestAudio_PrefersSpatialAndLosslessTracks()
    {
        MediaTechnicalInfo info = new()
        {
            Audio =
            [
                new AudioStreamInfo { Format = "Dolby Digital Plus 5.1", Channels = 6 },
                new AudioStreamInfo { Format = "Dolby Atmos", Channels = 6 },
                new AudioStreamInfo { Format = "Dolby Digital 5.1", Channels = 6 }
            ]
        };

        await Assert.That(MediaFormatSummary.BestAudio(info)!.Format).IsEqualTo("Dolby Atmos");
    }

    [Test]
    public async Task AspectRatio_UsesNearestCommonRatio()
    {
        await Assert.That(MediaFormatSummary.AspectRatio(1920, 804)).IsEqualTo("2.40:1");
        await Assert.That(MediaFormatSummary.AspectRatio(1920, 1080)).IsEqualTo("16:9");
        await Assert.That(MediaFormatSummary.AspectRatio(1440, 1080)).IsEqualTo("4:3");
    }

    [Test]
    public async Task SummaryLine_ProducesResolutionHdrAudioTriple()
    {
        MediaTechnicalInfo info = new()
        {
            Video = new VideoStreamInfo { Height = 1080, HdrFormat = "SDR", DynamicRange = MediaDynamicRange.Sdr },
            Audio = [new AudioStreamInfo { Format = "Dolby Digital 5.1" }]
        };

        // SDR is implicit and omitted from the compact summary.
        await Assert.That(MediaFormatSummary.SummaryLine(info)).IsEqualTo("1080p · Dolby Digital 5.1");
    }

    [Test]
    public async Task SummaryLine_UsesBestAudioAndCanOmitIt()
    {
        MediaTechnicalInfo info = new()
        {
            Video = new VideoStreamInfo { Width = 1920, Height = 804, HdrFormat = "Dolby Vision", DynamicRange = MediaDynamicRange.DolbyVision },
            Audio =
            [
                new AudioStreamInfo { Format = "Dolby Digital Plus 5.1" },
                new AudioStreamInfo { Format = "Dolby Atmos" }
            ]
        };

        await Assert.That(MediaFormatSummary.SummaryLine(info)).IsEqualTo("1080p · Dolby Vision · Dolby Atmos");
        await Assert.That(MediaFormatSummary.SummaryLine(info, includeAudio: false)).IsEqualTo("1080p · Dolby Vision");
    }
}

public class MediaInfoMapperTests
{
    static MediaInfoDto SampleMediaInfo() => new()
    {
        AudioBitrate = 640000,
        AudioChannels = 5.1,
        AudioCodec = "EAC3",
        AudioLanguages = "spa/spa/eng/fre/fre",
        AudioStreamCount = 5,
        VideoBitDepth = 10,
        VideoBitrate = 2722010,
        VideoCodec = "HEVC",
        VideoFps = 23.976,
        VideoDynamicRange = "HDR",
        VideoDynamicRangeType = "DV HDR10Plus",
        Resolution = "1920x804",
        RunTime = "1:39:51",
        ScanType = "Progressive",
        Subtitles = "spa/spa/fre/fre/fre/fre/eng"
    };

    [Test]
    public async Task TechnicalFrom_MapsArrMediaInfo()
    {
        MediaTechnicalInfo info = MediaInfoMapper.TechnicalFrom(SampleMediaInfo(), "/movies/1/file.mkv", 4472908038, null);

        await Assert.That(info.FileName).IsEqualTo("file.mkv");
        await Assert.That(info.Video!.CodecDisplay).IsEqualTo("HEVC");
        await Assert.That(info.Video!.BitDepth).IsEqualTo(10);
        await Assert.That(info.Video!.Resolution).IsEqualTo("1920x804");
        await Assert.That(info.Video!.AspectRatio).IsEqualTo("2.40:1");
        await Assert.That(info.Video!.HdrFormat).IsEqualTo("Dolby Vision");
        await Assert.That(info.Video!.FrameRate).IsEqualTo(23.976);
        await Assert.That(info.Audio.Count).IsEqualTo(1);
        await Assert.That(info.Audio[0].Format).IsEqualTo("Dolby Digital Plus 5.1");
        await Assert.That(info.Subtitles.Count).IsEqualTo(3); // Spanish, English, French (deduplicated)
        await Assert.That(info.Duration).IsEqualTo("1:39:51");
        await Assert.That(info.Probed).IsFalse();
    }

    [Test]
    public async Task QualityFrom_UsesFileQualityAndResolvesResolution()
    {
        FileQualityDto fileQuality = new()
        {
            Quality = new QualityDto { Name = "Bluray-1080p", Source = "bluray", Resolution = 1080 }
        };

        QualityInfo quality = MediaInfoMapper.QualityFrom(fileQuality, SampleMediaInfo());

        await Assert.That(quality.Resolution).IsEqualTo("1080p");
        await Assert.That(quality.Source).IsEqualTo(QualitySource.Bluray);
        await Assert.That(quality.HDR).IsEqualTo(QualityHDR.DolbyVision);
        await Assert.That(quality.VideoCodec).IsEqualTo("x265");
    }
}

public class FfprobeParserTests
{
    const string SampleJson = """
    {
      "streams": [
        {
          "index": 0,
          "codec_name": "hevc",
          "profile": "Main 10",
          "codec_type": "video",
          "width": 1920,
          "height": 804,
          "pix_fmt": "yuv420p10le",
          "level": 123,
          "color_space": "bt2020nc",
          "color_transfer": "smpte2084",
          "color_primaries": "bt2020",
          "r_frame_rate": "24000/1001",
          "avg_frame_rate": "24000/1001",
          "disposition": { "default": 1, "forced": 0 },
          "side_data_list": [ { "side_data_type": "DOVI configuration record", "dv_profile": 8 } ]
        },
        {
          "index": 1,
          "codec_name": "eac3",
          "profile": "Dolby Digital Plus + Dolby Atmos",
          "codec_type": "audio",
          "sample_rate": "48000",
          "channels": 6,
          "channel_layout": "5.1(side)",
          "bit_rate": "640000",
          "disposition": { "default": 1 },
          "tags": { "language": "eng", "title": "English DDP Atmos 5.1" }
        },
        {
          "index": 2,
          "codec_name": "aac",
          "codec_type": "audio",
          "sample_rate": "44100",
          "channels": 2,
          "bit_rate": "128000",
          "disposition": { "default": 0 },
          "tags": { "language": "fre", "title": "French" }
        },
        {
          "index": 3,
          "codec_name": "subrip",
          "codec_type": "subtitle",
          "disposition": { "default": 0, "forced": 1 },
          "tags": { "language": "eng", "title": "English SDH" }
        }
      ],
      "format": {
        "format_name": "matroska,webm",
        "duration": "5991.456000",
        "size": "4472908038",
        "bit_rate": "5972382"
      }
    }
    """;

    [Test]
    public async Task Parse_ExtractsContainerVideoAudioAndSubtitles()
    {
        MediaTechnicalInfo info = FfprobeParser.Parse(SampleJson, "/media/tv/movies/1/file.mkv");

        await Assert.That(info.Container).IsEqualTo("MKV");
        await Assert.That(info.Probed).IsTrue();
        await Assert.That(info.SizeBytes).IsEqualTo(4472908038);
        await Assert.That(info.OverallBitrate).IsEqualTo(5972382);
        await Assert.That(info.Duration).IsEqualTo("1:39:51");

        await Assert.That(info.Video!.CodecDisplay).IsEqualTo("HEVC");
        await Assert.That(info.Video!.Profile).IsEqualTo("Main 10");
        await Assert.That(info.Video!.BitDepth).IsEqualTo(10);
        await Assert.That(info.Video!.AspectRatio).IsEqualTo("2.40:1");
        await Assert.That(info.Video!.HdrFormat).IsEqualTo("Dolby Vision");
        await Assert.That(info.Video!.FrameRate).IsGreaterThan(23.9);
        await Assert.That(info.Video!.FrameRate).IsLessThan(24.0);
        await Assert.That(info.Video!.ColorSpace).IsEqualTo("bt2020nc");

        await Assert.That(info.Audio.Count).IsEqualTo(2);
        await Assert.That(info.Audio[0].Format).IsEqualTo("Dolby Atmos");
        await Assert.That(info.Audio[0].SampleRate).IsEqualTo(48000);
        await Assert.That(info.Audio[0].LanguageName).IsEqualTo("English");
        await Assert.That(info.Audio[1].Format).IsEqualTo("AAC 2.0");

        await Assert.That(info.Subtitles.Count).IsEqualTo(1);
        await Assert.That(info.Subtitles[0].LanguageName).IsEqualTo("English");
        await Assert.That(info.Subtitles[0].IsForced).IsTrue();
        await Assert.That(info.Subtitles[0].Codec).IsEqualTo("SubRip (SRT)");
    }

    [Test]
    public async Task Parse_HandlesMissingFormatGracefully()
    {
        MediaTechnicalInfo info = FfprobeParser.Parse("""{ "streams": [] }""", "/x/file.mkv", 1024);

        await Assert.That(info.Container).IsEqualTo("");
        await Assert.That(info.SizeBytes).IsEqualTo(1024);
        await Assert.That(info.Video).IsNull();
    }
}

public class ArrMediaPathMapperTests
{
    [Test]
    public async Task MapsArrContainerRootsToLocalRoots()
    {
        ArrMediaPathMapper mapper = new(
        [
            ("/movies", "/media/tv/movies"),
            ("/shows", "/media/tv/shows")
        ]);

        await Assert.That(mapper.MapToLocalPath("/movies/1273221/file.mkv"))
            .IsEqualTo("/media/tv/movies/1273221/file.mkv");
        await Assert.That(mapper.MapToLocalPath("/shows/121361/Season 3/ep.mkv"))
            .IsEqualTo("/media/tv/shows/121361/Season 3/ep.mkv");
    }

    [Test]
    public async Task UnknownRoot_ReturnsNullWhenFileMissing()
    {
        ArrMediaPathMapper mapper = new([("/movies", "/media/tv/movies")]);

        await Assert.That(mapper.MapToLocalPath("/something/else/file.mkv")).IsNull();
    }

    [Test]
    public async Task FactoryRoots_AreResolvedPerCall_SoStorageEditsApply()
    {
        string local = "/media/tv/movies";
        ArrMediaPathMapper mapper = new(() => [("/movies", local)]);

        await Assert.That(mapper.MapToLocalPath("/movies/a.mkv")).IsEqualTo("/media/tv/movies/a.mkv");

        local = "/mnt/new-media/movies";
        await Assert.That(mapper.MapToLocalPath("/movies/a.mkv")).IsEqualTo("/mnt/new-media/movies/a.mkv");
    }
}

public class MediaInfoServiceTests
{
    sealed class FakeProbe(MediaTechnicalInfo? result) : IMediaProbe
    {
        public int Calls { get; private set; }

        public Task<MediaTechnicalInfo?> ProbeAsync(string path, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    const string MovieJson = """
    {
      "id": 1,
      "title": "Scary Movie",
      "hasFile": true,
      "movieFile": {
        "id": 1,
        "path": "/movies/1273221/file.mkv",
        "size": 4472908038,
        "dateAdded": "2026-09-21T03:02:50Z",
        "quality": { "quality": { "id": 7, "name": "Bluray-1080p", "source": "bluray", "resolution": 1080 }, "revision": { "version": 1, "real": 0, "isRepack": false } },
        "mediaInfo": {
          "audioBitrate": 640000,
          "audioChannels": 5.1,
          "audioCodec": "EAC3",
          "audioLanguages": "eng",
          "audioStreamCount": 1,
          "videoBitDepth": 10,
          "videoBitrate": 2722010,
          "videoCodec": "HEVC",
          "videoFps": 23.976,
          "videoDynamicRange": "HDR",
          "videoDynamicRangeType": "DV HDR10Plus",
          "resolution": "1920x804",
          "runTime": "1:39:51",
          "scanType": "Progressive",
          "subtitles": "eng"
        }
      }
    }
    """;

    static RadarrClient Radarr(FakeHttpMessageHandler handler) =>
        new("http://localhost:7878/radarr", "stackarr", handler);

    static ArrMediaPathMapper Mapper() => new([("/movies", "/media/tv/movies")]);

    [Test]
    public async Task Movie_DeserializesSingularMovieFileWithFullMediaInfo()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(MovieJson);

        MovieDto? movie = await Radarr(handler).GetMovie(1);

        await Assert.That(movie!.MovieFile).IsNotNull();
        await Assert.That(movie.MovieFile!.MediaInfo!.VideoDynamicRangeType).IsEqualTo("DV HDR10Plus");
        await Assert.That(movie.MovieFile!.MediaInfo!.Resolution).IsEqualTo("1920x804");
        await Assert.That(movie.MovieFile!.Quality!.Quality!.Resolution).IsEqualTo(1080);
        await Assert.That(movie.MovieFile!.Quality!.Revision!.Version).IsEqualTo(1);
        await Assert.That(movie.MovieFile!.Quality!.Revision!.IsRepack).IsFalse();
    }

    [Test]
    public async Task GetMovieMediaInfo_MergesProbeWhenAvailable()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(MovieJson);

        FakeProbe probe = new(new MediaTechnicalInfo
        {
            FileName = "file.mkv",
            FilePath = "/media/tv/movies/1273221/file.mkv",
            Container = "MKV",
            SizeBytes = 4472908038,
            Duration = "1:39:51",
            OverallBitrate = 5972382,
            Video = new VideoStreamInfo { CodecDisplay = "HEVC", Profile = "Main 10", Height = 804, Resolution = "1920x804", HdrFormat = "Dolby Vision", DynamicRange = MediaDynamicRange.DolbyVision },
            Audio = [new AudioStreamInfo { Format = "Dolby Atmos", Channels = 6 }],
            Probed = true
        });

        MediaInfoService service = new(Radarr(handler), new SonarrClient("http://localhost:8989/sonarr", "stackarr", new FakeHttpMessageHandler()), probe, Mapper());

        MediaTechnicalInfo? info = await service.GetMovieMediaInfoAsync(1);

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Probed).IsTrue();
        await Assert.That(info.Container).IsEqualTo("MKV");
        await Assert.That(info.Video!.Profile).IsEqualTo("Main 10");
        await Assert.That(probe.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task GetMovieMediaInfo_FallsBackToArrWhenProbeUnavailable()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(MovieJson);

        FakeProbe probe = new(null);
        MediaInfoService service = new(Radarr(handler), new SonarrClient("http://localhost:8989/sonarr", "stackarr", new FakeHttpMessageHandler()), probe, Mapper());

        MediaTechnicalInfo? info = await service.GetMovieMediaInfoAsync(1);

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Probed).IsFalse();
        await Assert.That(info.Container).IsEqualTo("");
        await Assert.That(info.Video!.HdrFormat).IsEqualTo("Dolby Vision");
    }
}
