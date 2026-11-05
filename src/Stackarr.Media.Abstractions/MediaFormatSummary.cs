using System.Globalization;

namespace Stackarr.Media.Abstractions;

/// <summary>
/// Turns raw stream metadata (from the arrs or ffprobe) into the short, human friendly strings
/// used across the media UI, e.g. <c>2160p · Dolby Vision · Dolby Atmos TrueHD</c>.
/// </summary>
public static class MediaFormatSummary
{
    /// <summary>Compact "resolution · HDR · audio" line shown on cards and episode rows.</summary>
    public static string SummaryLine(MediaTechnicalInfo? info, QualityInfo? quality = null, bool includeAudio = true)
    {
        FormatSummaryParts parts = SummaryParts(info, quality, includeAudio);
        return string.Join(" · ", new[] { parts.Resolution, parts.Hdr, parts.Audio }.Where(p => p.Length > 0));
    }

    /// <summary>Structured form of <see cref="SummaryLine"/> so the UI can style each segment.</summary>
    public static FormatSummaryParts SummaryParts(MediaTechnicalInfo? info, QualityInfo? quality = null, bool includeAudio = true) => new(
        ResolveResolution(info, quality),
        HideSdr(info?.Video?.HdrFormat),
        includeAudio ? BestAudio(info)?.Format ?? "" : "");

    /// <summary>SDR is the implicit default, so the compact summary omits it, no HDR segment means SDR.</summary>
    static string HideSdr(string? hdr) =>
        string.IsNullOrWhiteSpace(hdr) || string.Equals(hdr, "SDR", StringComparison.OrdinalIgnoreCase) ? "" : hdr;

    /// <summary>An ordered resolution / HDR / audio triple.</summary>
    public readonly record struct FormatSummaryParts(string Resolution, string Hdr, string Audio)
    {
        public bool IsEmpty => Resolution.Length == 0 && Hdr.Length == 0 && Audio.Length == 0;
    }

    /// <summary>Resolves a display resolution from technical info, falling back to the quality record.</summary>
    public static string ResolveResolution(MediaTechnicalInfo? info, QualityInfo? quality = null)
    {
        if (quality is { Resolution.Length: > 0 })
            return quality.Resolution;

        if (info?.Video is { } video)
            return ResolutionFromDimensions(video.Width, video.Height);

        return "";
    }

    /// <summary>
    /// Derives a display resolution from coded dimensions. Width is preferred over height because
    /// widescreen crops (e.g. 1920x804) have a reduced height that would otherwise misreport the
    /// resolution as a lower tier.
    /// </summary>
    public static string ResolutionFromDimensions(int width, int height)
    {
        if (width > 0)
        {
            string fromWidth = ResolutionFromWidth(width);
            if (fromWidth.Length > 0)
                return fromWidth;
        }

        return ResolutionFromHeight(height);
    }

    public static string ResolutionFromHeight(int height) => height switch
    {
        >= 2000 => Resolutions.R2160p,
        >= 1000 => Resolutions.R1080p,
        >= 700 => Resolutions.R720p,
        >= 400 => Resolutions.R480p,
        _ => ""
    };

    public static string ResolutionFromWidth(int width) => width switch
    {
        >= 3400 => Resolutions.R2160p,
        >= 1700 => Resolutions.R1080p,
        >= 1100 => Resolutions.R720p,
        >= 700 => Resolutions.R480p,
        _ => ""
    };

    /// <summary>
    /// Picks the best audio track for the compact summary, Atmos/TrueHD over lossy codecs, and
    /// ties broken by the default flag and channel count. Returns <c>null</c> when there is no audio.
    /// </summary>
    public static AudioStreamInfo? BestAudio(MediaTechnicalInfo? info)
    {
        if (info?.Audio is not { Count: > 0 })
            return null;

        return info.Audio
            .OrderByDescending(AudioRank)
            .ThenByDescending(stream => stream.IsDefault)
            .ThenByDescending(stream => stream.Channels)
            .First();
    }

    static int AudioRank(AudioStreamInfo stream)
    {
        string hint = $"{stream.Format} {stream.Codec} {stream.Profile} {stream.Title}".ToLowerInvariant();
        if (hint.Contains("atmos") && hint.Contains("truehd"))
            return 100;
        if (hint.Contains("truehd"))
            return 95;
        if (hint.Contains("atmos"))
            return 90;
        if (hint.Contains("dts:x") || hint.Contains("dtsx"))
            return 85;
        if (hint.Contains("dts hd ma") || hint.Contains("dts-hd ma"))
            return 80;
        if (hint.Contains("dts hd") || hint.Contains("dts-hd"))
            return 75;
        if (hint.Contains("dts"))
            return 70;
        if (hint.Contains("flac"))
            return 60;
        if (hint.Contains("pcm"))
            return 55;
        if (hint.Contains("dolby digital plus"))
            return 50;
        if (hint.Contains("dolby digital"))
            return 45;
        if (hint.Contains("aac"))
            return 40;
        if (hint.Contains("opus"))
            return 35;
        return 10;
    }

    /// <summary>
    /// Maps the arr <c>videoDynamicRange</c> / <c>videoDynamicRangeType</c> pair (and ffprobe's
    /// own hints) to a single friendly label, the best format wins (Dolby Vision over HDR10+).
    /// </summary>
    public static (string Text, MediaDynamicRange Range) Hdr(
        string? dynamicRange,
        string? dynamicRangeType,
        bool hasDolbyVisionSideData = false,
        bool hasMasteringDisplay = false)
    {
        string range = (dynamicRange ?? "").ToLowerInvariant();
        string type = (dynamicRangeType ?? "").ToLowerInvariant();

        bool dolbyVision = hasDolbyVisionSideData || type.Contains("dovi") || type.Contains("dv")
            || range.Contains("dolby vision") || range.Contains("dovi");
        bool hdr10Plus = type.Contains("hdr10plus") || type.Contains("hdr10+") || type.Contains("hdr10p");
        bool hdr10 = (!hdr10Plus && (type.Contains("hdr10") || range == "hdr" || range.Contains("hdr10")))
            || hasMasteringDisplay;
        bool hlg = range.Contains("hlg") || type.Contains("hlg");

        if (dolbyVision)
            return ("Dolby Vision", MediaDynamicRange.DolbyVision);
        if (hdr10Plus)
            return ("HDR10+", MediaDynamicRange.Hdr10Plus);
        if (hdr10)
            return ("HDR", MediaDynamicRange.Hdr10);
        if (hlg)
            return ("HLG", MediaDynamicRange.Hlg);

        return ("SDR", MediaDynamicRange.Sdr);
    }

    /// <summary>Friendly audio format, e.g. <c>Dolby Atmos TrueHD</c> or <c>Dolby Digital 5.1</c>.</summary>
    public static string FriendlyAudio(string? codec, double channels, string? profile = null, string? title = null)
    {
        string raw = codec ?? "";
        string lower = raw.ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');
        string hints = $"{profile} {title}".ToLowerInvariant();

        bool atmos = hints.Contains("atmos") || lower.Contains("atmos");
        bool dtsx = hints.Contains("dts:x") || hints.Contains("dts x") || lower.Contains("dtsx")
            || lower.Contains("dts x");

        string name = lower switch
        {
            var c when c.Contains("truehd") => atmos ? "Dolby Atmos TrueHD" : "Dolby TrueHD",
            var c when c.Contains("eac3") || c.Contains("ddp") || c.Contains("dolby digital plus") =>
                atmos ? "Dolby Atmos" : "Dolby Digital Plus",
            var c when c.Contains("ac3") || c.Contains("dolby digital") =>
                atmos ? "Dolby Atmos" : "Dolby Digital",
            var c when dtsx || c.Contains("dtsx") => "DTS:X",
            var c when c.Contains("dts hd ma") || c.Contains("dtshd ma") || c.Contains("dts hd") => "DTS-HD MA",
            var c when c.StartsWith("dts") => "DTS",
            var c when c.Contains("aac") => "AAC",
            var c when c.Contains("flac") => "FLAC",
            var c when c.Contains("opus") => "Opus",
            var c when c.Contains("mp3") => "MP3",
            var c when c.Contains("pcm") || c.Contains("lpcm") => "PCM",
            var c when c.Contains("vorbis") => "Vorbis",
            _ => raw.Length == 0 ? "Unknown" : raw.ToUpperInvariant()
        };

        // Spatial formats imply their layout; don't clutter them with "5.1".
        if (name.Contains("Atmos") || name == "DTS:X")
            return name;

        string channelsText = FormatChannels(channels);
        return channelsText.Length > 0 ? $"{name} {channelsText}" : name;
    }

    public static string FormatChannels(double channels)
    {
        if (channels <= 0)
            return "";
        double rounded = Math.Round(channels, 1);
        return rounded switch
        {
            1 => "1.0",
            2 => "2.0",
            3 => "2.1",
            6 => "5.1",
            7 => "6.1",
            8 => "7.1",
            _ => rounded.ToString("0.#", CultureInfo.InvariantCulture)
        };
    }

    public static string FriendlyVideoCodec(string? codec)
    {
        string lower = (codec ?? "").ToLowerInvariant().Replace('.', ' ').Trim();
        return lower switch
        {
            "hevc" or "h265" or "h 265" or "x265" => "HEVC",
            "avc" or "h264" or "h 264" or "x264" => "H.264",
            "av1" => "AV1",
            "vp9" => "VP9",
            "vp8" => "VP8",
            "mpeg2video" or "mpeg2" => "MPEG-2",
            "mpeg4" => "MPEG-4",
            "vc1" or "vc 1" => "VC-1",
            "theora" => "Theora",
            _ => (codec ?? "").ToUpperInvariant()
        };
    }

    /// <summary>Maps an ffprobe <c>format_name</c> (comma separated) to a short container label.</summary>
    public static string FriendlyContainer(string? formatName)
    {
        if (string.IsNullOrWhiteSpace(formatName))
            return "";
        string first = formatName.Split(',')[0].Trim().ToLowerInvariant();
        return first switch
        {
            "matroska" or "webm" => "MKV",
            "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2" => "MP4",
            "mpegts" => "MPEG-TS",
            "mpeg" or "mpegvideo" => "MPEG",
            "avi" => "AVI",
            "asf" => "ASF",
            "flv" => "FLV",
            "ogg" => "OGG",
            "flac" => "FLAC",
            "mp3" => "MP3",
            "wav" => "WAV",
            "aac" => "AAC",
            _ => first.ToUpperInvariant()
        };
    }

    public static string FriendlySubtitleCodec(string? codec)
    {
        string lower = (codec ?? "").ToLowerInvariant().Replace('_', ' ').Trim();
        return lower switch
        {
            "subrip" or "srt" => "SubRip (SRT)",
            "ass" or "ssa" => "ASS/SSA",
            "mov text" => "MOV Text",
            "hdmv pgs" or "pgs" => "PGS",
            "dvd sub" or "dvd subtitle" => "VobSub",
            "webvtt" or "vtt" => "WebVTT",
            "dvb sub" or "dvbsub" => "DVB",
            _ => (codec ?? "").ToUpperInvariant()
        };
    }

    /// <summary>Nearest common display aspect ratio, e.g. <c>2.40:1</c>.</summary>
    public static string AspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return "";

        double ratio = (double)width / height;
        (double Value, string Label)[] known =
        [
            (4.0 / 3, "4:3"),
            (3.0 / 2, "3:2"),
            (16.0 / 10, "16:10"),
            (5.0 / 3, "5:3"),
            (16.0 / 9, "16:9"),
            (1.85, "1.85:1"),
            (2.0, "2.00:1"),
            (2.20, "2.20:1"),
            (2.35, "2.35:1"),
            (2.39, "2.40:1"),
            (2.40, "2.40:1"),
            (2.76, "2.76:1")
        ];

        (double Value, string Label) best = known[0];
        double bestDelta = double.MaxValue;
        foreach ((double Value, string Label) candidate in known)
        {
            double delta = Math.Abs(candidate.Value - ratio);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = candidate;
            }
        }

        return bestDelta <= 0.06 ? best.Label : ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":1";
    }

    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
            return "";
        TimeSpan span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }

    public static string FormatBitrate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
            return "";
        if (bitsPerSecond >= 1_000_000)
            return (bitsPerSecond / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps";
        if (bitsPerSecond >= 1_000)
            return (bitsPerSecond / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kbps";
        return bitsPerSecond + " bps";
    }

    public static string FormatSampleRate(int hertz)
    {
        if (hertz <= 0)
            return "";
        if (hertz % 1000 == 0)
            return $"{hertz / 1000} kHz";
        return (hertz / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz";
    }

    static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["eng"] = "English",
        ["ja"] = "Japanese", ["jpn"] = "Japanese",
        ["fr"] = "French", ["fre"] = "French", ["fra"] = "French",
        ["es"] = "Spanish", ["spa"] = "Spanish",
        ["de"] = "German", ["ger"] = "German", ["deu"] = "German",
        ["it"] = "Italian", ["ita"] = "Italian",
        ["pt"] = "Portuguese", ["por"] = "Portuguese",
        ["ru"] = "Russian", ["rus"] = "Russian",
        ["zh"] = "Chinese", ["chi"] = "Chinese", ["zho"] = "Chinese",
        ["ko"] = "Korean", ["kor"] = "Korean",
        ["ar"] = "Arabic", ["ara"] = "Arabic",
        ["hi"] = "Hindi", ["hin"] = "Hindi",
        ["nl"] = "Dutch", ["dut"] = "Dutch", ["nld"] = "Dutch",
        ["sv"] = "Swedish", ["swe"] = "Swedish",
        ["no"] = "Norwegian", ["nor"] = "Norwegian",
        ["da"] = "Danish", ["dan"] = "Danish",
        ["fi"] = "Finnish", ["fin"] = "Finnish",
        ["pl"] = "Polish", ["pol"] = "Polish",
        ["tr"] = "Turkish", ["tur"] = "Turkish",
        ["cs"] = "Czech", ["cze"] = "Czech", ["ces"] = "Czech",
        ["el"] = "Greek", ["gre"] = "Greek", ["ell"] = "Greek",
        ["he"] = "Hebrew", ["heb"] = "Hebrew",
        ["hu"] = "Hungarian", ["hun"] = "Hungarian",
        ["ro"] = "Romanian", ["rum"] = "Romanian", ["ron"] = "Romanian",
        ["th"] = "Thai", ["tha"] = "Thai",
        ["vi"] = "Vietnamese", ["vie"] = "Vietnamese",
        ["uk"] = "Ukrainian", ["ukr"] = "Ukrainian",
        ["id"] = "Indonesian", ["ind"] = "Indonesian",
        ["ms"] = "Malay", ["may"] = "Malay", ["msa"] = "Malay",
        ["bn"] = "Bengali", ["ben"] = "Bengali",
        ["ta"] = "Tamil", ["tam"] = "Tamil",
        ["te"] = "Telugu", ["tel"] = "Telugu",
        ["fa"] = "Persian", ["per"] = "Persian", ["fas"] = "Persian",
        ["hidden"] = "Hidden"
    };

    /// <summary>Display name for an ISO 639 language code, falling back to the raw value.</summary>
    public static string LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "";
        string key = code.Trim();
        return LanguageNames.TryGetValue(key, out string? name) ? name : key;
    }

    /// <summary>Maps a slash/comma separated arr language list (e.g. <c>spa/spa/eng</c>) to unique names.</summary>
    public static List<string> LanguageNamesFromList(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
            return [];

        List<string> names = [];
        foreach (string part in list.Split(['/', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = LanguageName(part);
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }
        return names;
    }
}
