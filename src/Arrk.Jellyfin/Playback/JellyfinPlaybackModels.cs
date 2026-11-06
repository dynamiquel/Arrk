using System.Text.Json.Serialization;

namespace Arrk.Jellyfin.Playback;

/// <summary>
/// What the browser reports it can decode. The client is the source of truth (capabilities vary
/// by browser/OS/hardware), so Arrk never guesses from a User-Agent or a version string, it
/// probes the running browser and composes the Jellyfin device profile from these facts.
/// </summary>
public sealed class BrowserCapabilities
{
    /// <summary>True when the browser plays HLS natively (Safari), so no hls.js is needed.</summary>
    public bool NativeHls { get; set; }

    public bool Hevc { get; set; }
    public bool Av1 { get; set; }
    public bool Vp9 { get; set; }
    public bool Flac { get; set; }
    public bool Opus { get; set; }
    public bool Ac3 { get; set; }
    public bool Eac3 { get; set; }

    /// <summary>Upper bound used for the device profile bitrate (bytes/s). 0 = server default.</summary>
    public long MaxStreamingBitrate { get; set; }
}

/// <summary>
/// What the player page needs to start playback. This is Arrk's own shape, the UI never sees
/// a Jellyfin DTO, which keeps every Jellyfin-specific detail inside this module.
/// </summary>
public sealed class PlaybackPlan
{
    public string SessionId { get; set; } = "";

    /// <summary>Arrk proxy URL the player should load (relative to the site root).</summary>
    public string StreamUrl { get; set; } = "";

    /// <summary>True when Jellyfin is remuxing/transcoding rather than serving the original file.</summary>
    public bool IsTranscode { get; set; }

    /// <summary>HLS when the stream is a playlist; "native" when the browser can load it directly.</summary>
    public string Protocol { get; set; } = "hls";

    public long RunTimeTicks { get; set; }

    /// <summary>Resume position (ticks) the player should seek to.</summary>
    public long StartTicks { get; set; }

    /// <summary>The Jellyfin media version these tracks belong to.</summary>
    public string MediaSourceId { get; set; } = "";

    /// <summary>The audio stream Jellyfin is actually playing.</summary>
    public int? SelectedAudioIndex { get; set; }

    /// <summary>
    /// The subtitle stream the player should show, or <c>null</c> when subtitles are off. Text
    /// subtitles are side-loaded by the player; image subtitles are burned in by Jellyfin.
    /// </summary>
    public int? SelectedSubtitleIndex { get; set; }

    /// <summary>True when Jellyfin is burning the selected subtitle into the video stream.</summary>
    public bool SubtitlesBurnedIn { get; set; }

    public List<PlaybackAudioTrack> AudioTracks { get; set; } = [];
    public List<PlaybackSubtitle> Subtitles { get; set; } = [];
}

public sealed class PlaybackAudioTrack
{
    public int Index { get; set; }
    public string DisplayTitle { get; set; } = "";
    public string Language { get; set; } = "";
    public string Codec { get; set; } = "";
    public int? Channels { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class PlaybackSubtitle
{
    public int Index { get; set; }
    public string DisplayTitle { get; set; } = "";
    public string Language { get; set; } = "";
    public string Codec { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsExternal { get; set; }

    /// <summary>
    /// True for text subtitles, which Arrk side-loads as WebVTT so they can be switched
    /// without restarting the transcode. Image subtitles (PGS/VOBSUB) must be burned in.
    /// </summary>
    public bool IsText { get; set; }

    /// <summary>Arrk proxy URL for the converted WebVTT track. Text subtitles only.</summary>
    public string? Url { get; set; }
}

//  Jellyfin wire contracts (only the fields Arrk uses; unknown members ignored)

public sealed class JellyfinPlaybackInfoRequest
{
    public string? UserId { get; set; }
    public JellyfinDeviceProfile? DeviceProfile { get; set; }
    public long? StartTimeTicks { get; set; }
    public int? AudioStreamIndex { get; set; }
    public int? SubtitleStreamIndex { get; set; }
    public string? MediaSourceId { get; set; }
    public long? MaxStreamingBitrate { get; set; }
    public bool EnableDirectPlay { get; set; }
    public bool EnableDirectStream { get; set; }
    public bool EnableTranscoding { get; set; } = true;
    public bool AllowVideoStreamCopy { get; set; } = true;
    public bool AllowAudioStreamCopy { get; set; } = true;
    public bool IsPlayback { get; set; } = true;
    public bool AutoOpenLiveStream { get; set; }
}

public sealed class JellyfinPlaybackInfoResponse
{
    public List<JellyfinMediaSource> MediaSources { get; set; } = [];
    public string? PlaySessionId { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class JellyfinMediaSource
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Container { get; set; }
    public long? RunTimeTicks { get; set; }
    public long? Size { get; set; }
    public bool SupportsDirectPlay { get; set; }
    public bool SupportsDirectStream { get; set; }
    public bool SupportsTranscoding { get; set; }
    public string? DirectStreamUrl { get; set; }
    public string? TranscodingUrl { get; set; }
    public string? TranscodingContainer { get; set; }
    public string? TranscodingSubProtocol { get; set; }
    public List<JellyfinMediaStream> MediaStreams { get; set; } = [];
}

public sealed class JellyfinMediaStream
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? DisplayTitle { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsExternal { get; set; }
    public int? Height { get; set; }
    public int? Width { get; set; }
    public int? Channels { get; set; }
}

// The device profile shape. Only what is needed to make Jellyfin choose the right play method.

public sealed class JellyfinDeviceProfile
{
    public string? Name { get; set; }
    public long? MaxStreamingBitrate { get; set; }
    public List<JellyfinDirectPlayProfile> DirectPlayProfiles { get; set; } = [];
    public List<JellyfinTranscodingProfile> TranscodingProfiles { get; set; } = [];
    public List<JellyfinCodecProfile> CodecProfiles { get; set; } = [];
    public List<JellyfinSubtitleProfile> SubtitleProfiles { get; set; } = [];
}

public sealed class JellyfinDirectPlayProfile
{
    public string? Container { get; set; }
    public string? Type { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
}

public sealed class JellyfinTranscodingProfile
{
    public string? Container { get; set; }
    public string? Type { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Protocol { get; set; }
    public string? Context { get; set; }
    public int? MaxAudioChannels { get; set; }
    public bool CopyTimestamps { get; set; }
    public string? TranscodeSeekInfo { get; set; }
    public bool EstimateContentLength { get; set; }
    public bool EnableMpegtsM2TsMode { get; set; }
}

public sealed class JellyfinCodecProfile
{
    public string? Type { get; set; }
    public string? Codec { get; set; }
    public string? Container { get; set; }
    public List<JellyfinProfileCondition> Conditions { get; set; } = [];
}

public sealed class JellyfinProfileCondition
{
    public string? Condition { get; set; }
    public string? Property { get; set; }
    public string? Value { get; set; }
    public bool IsRequired { get; set; }
}

public sealed class JellyfinSubtitleProfile
{
    public string? Format { get; set; }
    public string? Method { get; set; }
}

/// <summary>
/// The following episode of the same series, used for the end-of-episode autoplay overlay.
/// <c>null</c> from the endpoint means the series has no following episode.
/// </summary>
public sealed class PlaybackNextEpisode
{
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Secondary line, e.g. "Re:ZERO · S01E03".</summary>
    public string Subtitle { get; set; } = "";

    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public long RunTimeTicks { get; set; }

    /// <summary>Arrk-proxied still image for the overlay (same-origin, auth via cookie).</summary>
    public string ImageUrl { get; set; } = "";

    /// <summary>Jellyfin deep link for the "watch in Jellyfin" action.</summary>
    public string JellyfinUrl { get; set; } = "";
}

/// <summary>Progress reported by the player. Kept separate from Jellyfin's DTOs.</summary>
public sealed class PlaybackProgressUpdate
{
    public long PositionTicks { get; set; }
    public bool IsPaused { get; set; }
}

/// <summary>Body of the "start playback" request from the player page.</summary>
public sealed class PlaybackStartRequest
{
    public BrowserCapabilities Capabilities { get; set; } = new();
    public long? StartTicks { get; set; }
    public int? AudioStreamIndex { get; set; }
    public int? SubtitleStreamIndex { get; set; }
}

/// <summary>
/// Display metadata for the player header: a primary line and an optional secondary line
/// (e.g. "Valar Dohaeris" / "Game of Thrones · S03E01").
/// </summary>
public sealed class PlaybackItemInfo
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";

    /// <summary>Jellyfin item id, used as a stable player identity.</summary>
    public string ItemId { get; set; } = "";

    public bool IsMovie { get; set; }

    public int TmdbId { get; set; }

    public int TvdbId { get; set; }

    public string ImdbId { get; set; } = "";

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }
}
