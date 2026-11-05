namespace Stackarr.Media.Abstractions;

/// <summary>
/// Dynamic range classification for a video stream. Kept as a small enum so the UI can colour
/// the HDR chip without re-parsing the friendly text.
/// </summary>
public enum MediaDynamicRange
{
    Sdr,
    Hdr10,
    Hdr10Plus,
    DolbyVision,
    Hlg
}

/// <summary>Detailed video stream metadata for a media file.</summary>
public class VideoStreamInfo
{
    public string Codec { get; init; } = "";
    public string Profile { get; init; } = "";
    public string Level { get; init; } = "";
    public int BitDepth { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Coded resolution, e.g. <c>1920x804</c>.</summary>
    public string Resolution { get; init; } = "";

    /// <summary>Display aspect ratio, e.g. <c>2.40:1</c> or <c>16:9</c>.</summary>
    public string AspectRatio { get; init; } = "";

    public double FrameRate { get; init; }
    public long Bitrate { get; init; }

    public MediaDynamicRange DynamicRange { get; init; }

    /// <summary>Friendly dynamic range, e.g. <c>Dolby Vision</c>, <c>HDR10+</c> or <c>SDR</c>.</summary>
    public string HdrFormat { get; init; } = "SDR";

    /// <summary>Human friendly codec, e.g. <c>HEVC</c>, <c>H.264</c>, <c>AV1</c>.</summary>
    public string CodecDisplay { get; init; } = "";

    public string ColorSpace { get; init; } = "";
    public string ColorPrimaries { get; init; } = "";
    public string ColorTransfer { get; init; } = "";
    public string ChromaSubsampling { get; init; } = "";
    public string ScanType { get; init; } = "";
}

/// <summary>A single audio track within a media file.</summary>
public class AudioStreamInfo
{
    public int Index { get; init; }
    public string Codec { get; init; } = "";
    public string Profile { get; init; } = "";
    public double Channels { get; init; }
    public string ChannelLayout { get; init; } = "";
    public int SampleRate { get; init; }
    public long Bitrate { get; init; }

    /// <summary>ISO 639 language code as reported by the container (e.g. <c>eng</c>).</summary>
    public string Language { get; init; } = "";

    /// <summary>Display name for <see cref="Language"/>, e.g. <c>English</c>.</summary>
    public string LanguageName { get; init; } = "";

    public string Title { get; init; } = "";
    public bool IsDefault { get; init; }

    /// <summary>Friendly format, e.g. <c>Dolby Atmos TrueHD</c> or <c>Dolby Digital 5.1</c>.</summary>
    public string Format { get; init; } = "";
}

/// <summary>A single subtitle track within a media file.</summary>
public class SubtitleStreamInfo
{
    public string Codec { get; init; } = "";
    public string Language { get; init; } = "";
    public string LanguageName { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsForced { get; init; }
    public bool IsDefault { get; init; }
    public bool IsExternal { get; init; }
}

/// <summary>
/// Rich technical description of a single media file (movie or episode). Populated from the
/// arr <c>mediaInfo</c> payload and, where the file is reachable from Stackarr, enriched with a
/// full ffprobe scan. This is the "media info" surface shown in the UI.
/// </summary>
public class MediaTechnicalInfo
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Container { get; init; } = "";
    public long SizeBytes { get; init; }

    /// <summary>Duration as reported by the scanner, e.g. <c>1:39:51</c>.</summary>
    public string Duration { get; init; } = "";

    public long OverallBitrate { get; init; }
    public VideoStreamInfo? Video { get; init; }
    public List<AudioStreamInfo> Audio { get; init; } = [];
    public List<SubtitleStreamInfo> Subtitles { get; init; } = [];

    /// <summary>True when the data came from a full ffprobe scan rather than the arr summary.</summary>
    public bool Probed { get; init; }

    public bool HasData => Video is not null || Audio.Count > 0 || Subtitles.Count > 0 || !string.IsNullOrEmpty(Container);

    /// <summary>Compact "resolution · HDR · audio" line used on cards and episode rows.</summary>
    public string Summary => MediaFormatSummary.SummaryLine(this);
}
