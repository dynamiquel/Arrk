namespace Arrk.Jellyfin.Playback;

/// <summary>
/// Turns what the browser reported into a Jellyfin <c>DeviceProfile</c>. This is a
/// pure function with no I/O: it is the part most likely to need adjusting as Jellyfin or browsers
/// evolve, so it is isolated and unit-tested without a running server.
/// </summary>
/// <remarks>
/// The rules encoded here are the difference between "works" and "black screen":
/// <list type="bullet">
/// <item>Browsers cannot play the MKV container in a <c>&lt;video&gt;</c> element, so MKV is
/// <b>never</b> declared as direct play, Jellyfin must remux it (cheap, no video re-encode).</item>
/// <item>HEVC is only declared when the browser reports it can hardware-decode it; otherwise
/// Jellyfin transcodes to H.264.</item>
/// <item>DTS/TrueHD are never declared (no browser decodes them); audio is transcoded to AAC.</item>
/// <item>All output is HLS fMP4, which both Safari (natively) and Chromium (via hls.js/MSE) play.</item>
/// </list>
/// </remarks>
public static class JellyfinDeviceProfileFactory
{
    public const string ProfileName = "Arrk Web Player";

    /// <summary>Container for the HLS output. Jellyfin emits fragmented MP4 segments.</summary>
    const string HlsContainer = "mp4";

    public static JellyfinDeviceProfile Build(BrowserCapabilities caps)
    {
        List<string> video = ["h264"];
        if (caps.Hevc) video.Add("hevc");
        if (caps.Av1) video.Add("av1");
        if (caps.Vp9) video.Add("vp9");

        List<string> audio = ["aac", "mp3"];
        if (caps.Ac3) audio.Add("ac3");
        if (caps.Eac3) audio.Add("eac3");
        if (caps.Flac) audio.Add("flac");
        if (caps.Opus) audio.Add("opus");

        string videoCodecs = string.Join(',', video);
        string audioCodecs = string.Join(',', audio);

        JellyfinDeviceProfile profile = new()
        {
            Name = ProfileName,
            MaxStreamingBitrate = caps.MaxStreamingBitrate > 0 ? caps.MaxStreamingBitrate : null,

            // Direct play is advertised for the containers browsers genuinely handle from a
            // <video> src. MKV is absent so Jellyfin remuxes it.
            DirectPlayProfiles =
            [
                new JellyfinDirectPlayProfile { Container = "mp4", Type = "Video", VideoCodec = videoCodecs, AudioCodec = audioCodecs },
                new JellyfinDirectPlayProfile { Container = "webm", Type = "Video", VideoCodec = "vp9,av1,opus", AudioCodec = "opus,vorbis" },
                new JellyfinDirectPlayProfile { Container = "mp3", Type = "Audio", AudioCodec = "mp3,aac" }
            ],

            // Everything else is delivered as HLS fMP4. Listing the browser's codecs lets Jellyfin
            // copy (remux) when possible and only re-encode when it must.
            TranscodingProfiles =
            [
                new JellyfinTranscodingProfile
                {
                    Container = HlsContainer,
                    Type = "Video",
                    VideoCodec = videoCodecs,
                    AudioCodec = "aac",
                    Protocol = "hls",
                    Context = "Streaming",
                    TranscodeSeekInfo = "Auto",
                    CopyTimestamps = false,
                    EnableMpegtsM2TsMode = false
                },
                new JellyfinTranscodingProfile
                {
                    Container = HlsContainer,
                    Type = "Audio",
                    // Jellyfin validates VideoCodec as required even for audio profiles.
                    VideoCodec = "",
                    AudioCodec = "aac",
                    Protocol = "hls",
                    Context = "Streaming",
                    TranscodeSeekInfo = "Auto"
                }
            ],

            // Text subtitles are always side-loaded, which Arrk serves as WebVTT (the one
            // format <track> accepts). Declaring the source codecs as External keeps Jellyfin from
            // burning them into the video; image subtitles match none of these profiles and are
            // burned in only when the user explicitly selects them.
            SubtitleProfiles = TextSubtitleFormats
                .Select(format => new JellyfinSubtitleProfile { Format = format, Method = "External" })
                .ToList()
        };

        return profile;
    }

    /// <summary>Text subtitle codecs Jellyfin can convert to WebVTT for side-loading.</summary>
    static readonly string[] TextSubtitleFormats =
    [
        "vtt", "srt", "subrip", "ass", "ssa", "mov_text", "ttml",
        "sami", "microdvd", "mpl2", "realtext", "subviewer", "jacosub", "pjs"
    ];
}
