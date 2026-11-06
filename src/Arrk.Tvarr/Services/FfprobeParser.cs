using System.Globalization;
using System.Text.Json;
using Arrk.Core.Models;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Parses the JSON emitted by <c>ffprobe -show_format -show_streams</c> into the shared
/// <see cref="MediaTechnicalInfo"/> model. Split out from the process runner so the mapping can
/// be unit tested with captured ffprobe output.
/// </summary>
public static class FfprobeParser
{
    public static MediaTechnicalInfo Parse(string json, string path, long fallbackSize = 0)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        JsonElement format = root.TryGetProperty("format", out JsonElement f) ? f : default;
        JsonElement streams = root.TryGetProperty("streams", out JsonElement s) ? s : default;

        string container = format.ValueKind == JsonValueKind.Object
            ? MediaFormatSummary.FriendlyContainer(GetString(format, "format_name"))
            : "";

        double durationSeconds = format.ValueKind == JsonValueKind.Object
            ? GetDouble(format, "duration")
            : 0;

        long size = format.ValueKind == JsonValueKind.Object ? GetLong(format, "size") : 0;
        if (size <= 0)
            size = fallbackSize;

        long overallBitrate = format.ValueKind == JsonValueKind.Object ? GetLong(format, "bit_rate") : 0;

        VideoStreamInfo? video = null;
        List<AudioStreamInfo> audio = [];
        List<SubtitleStreamInfo> subtitles = [];

        if (streams.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                string type = GetString(stream, "codec_type");
                switch (type)
                {
                    case "video":
                        video ??= ParseVideo(stream);
                        break;
                    case "audio":
                        audio.Add(ParseAudio(stream));
                        break;
                    case "subtitle":
                        subtitles.Add(ParseSubtitle(stream));
                        break;
                }
            }
        }

        // Duration: prefer the container; ffprobe also exposes per-stream duration but the
        // container value is the accurate overall runtime.
        return new MediaTechnicalInfo
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            Container = container,
            SizeBytes = size,
            Duration = MediaFormatSummary.FormatDuration(durationSeconds),
            OverallBitrate = overallBitrate,
            Video = video,
            Audio = audio,
            Subtitles = subtitles,
            Probed = true
        };
    }

    static VideoStreamInfo ParseVideo(JsonElement stream)
    {
        int width = (int)GetLong(stream, "width");
        int height = (int)GetLong(stream, "height");

        bool hasDolbyVision = false;
        bool hasHdr10Plus = false;
        bool hasMasteringDisplay = false;
        if (stream.TryGetProperty("side_data_list", out JsonElement sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in sideData.EnumerateArray())
            {
                string kind = GetString(item, "side_data_type").ToLowerInvariant();
                if (kind.Contains("dovi") || kind.Contains("dolby vision"))
                    hasDolbyVision = true;
                else if (kind.Contains("hdr10+") || kind.Contains("hdr10 plus") || kind.Contains("st 2094"))
                    hasHdr10Plus = true;
                else if (kind.Contains("mastering display") || kind.Contains("content light"))
                    hasMasteringDisplay = true;
            }
        }

        string transfer = GetString(stream, "color_transfer").ToLowerInvariant();
        if (transfer is "smpte2084" or "smpte-st-2084")
            hasMasteringDisplay = true; // PQ implies HDR10 unless Dolby Vision overrides it.

        string dynamicRange = transfer == "arib-std-b67" ? "HLG" : "";
        string dynamicRangeType = hasHdr10Plus ? "HDR10+" : "";

        (string hdrText, MediaDynamicRange hdrRange) = MediaFormatSummary.Hdr(
            dynamicRange, dynamicRangeType, hasDolbyVision, hasMasteringDisplay);

        string pixFmt = GetString(stream, "pix_fmt");
        int bitDepth = DetectBitDepth(pixFmt, stream);

        double frameRate = ParseFraction(GetString(stream, "avg_frame_rate"));
        if (frameRate <= 0)
            frameRate = ParseFraction(GetString(stream, "r_frame_rate"));

        return new VideoStreamInfo
        {
            Codec = GetString(stream, "codec_name"),
            CodecDisplay = MediaFormatSummary.FriendlyVideoCodec(GetString(stream, "codec_name")),
            Profile = GetString(stream, "profile"),
            Level = GetString(stream, "level"),
            BitDepth = bitDepth,
            Width = width,
            Height = height,
            Resolution = width > 0 && height > 0 ? $"{width}x{height}" : "",
            AspectRatio = MediaFormatSummary.AspectRatio(width, height),
            FrameRate = frameRate,
            Bitrate = GetLong(stream, "bit_rate"),
            DynamicRange = hdrRange,
            HdrFormat = hdrText,
            ColorSpace = GetString(stream, "color_space"),
            ColorPrimaries = GetString(stream, "color_primaries"),
            ColorTransfer = GetString(stream, "color_transfer"),
            ChromaSubsampling = pixFmt,
            ScanType = GetString(stream, "field_order") is { Length: > 0 } fieldOrder && fieldOrder != "progressive"
                ? fieldOrder
                : "Progressive"
        };
    }

    static AudioStreamInfo ParseAudio(JsonElement stream)
    {
        string codec = GetString(stream, "codec_name");
        string profile = GetString(stream, "profile");
        string title = GetString(stream, "tags", "title");
        string language = GetString(stream, "tags", "language");
        double channels = GetDouble(stream, "channels");

        return new AudioStreamInfo
        {
            Index = (int)GetLong(stream, "index"),
            Codec = codec,
            Profile = profile,
            Channels = channels,
            ChannelLayout = GetString(stream, "channel_layout"),
            SampleRate = (int)GetLong(stream, "sample_rate"),
            Bitrate = GetLong(stream, "bit_rate"),
            Language = language,
            LanguageName = MediaFormatSummary.LanguageName(language),
            Title = title,
            IsDefault = GetDisposition(stream, "default"),
            Format = MediaFormatSummary.FriendlyAudio(codec, channels, profile, title)
        };
    }

    static SubtitleStreamInfo ParseSubtitle(JsonElement stream)
    {
        string language = GetString(stream, "tags", "language");
        return new SubtitleStreamInfo
        {
            Codec = MediaFormatSummary.FriendlySubtitleCodec(GetString(stream, "codec_name")),
            Language = language,
            LanguageName = MediaFormatSummary.LanguageName(language),
            Title = GetString(stream, "tags", "title"),
            IsForced = GetDisposition(stream, "forced"),
            IsDefault = GetDisposition(stream, "default")
        };
    }

    static int DetectBitDepth(string pixFmt, JsonElement stream)
    {
        long raw = GetLong(stream, "bits_per_raw_sample");
        if (raw > 0)
            return (int)raw;

        if (string.IsNullOrEmpty(pixFmt))
            return 0;

        // yuv420p10le / yuv420p12le / yuv420p16le carry the depth before the endian suffix.
        string trimmed = pixFmt.TrimEnd('l', 'e', 'b', 'B', 'E', 'L');
        int index = trimmed.Length;
        while (index > 0 && char.IsDigit(trimmed[index - 1]))
            index--;

        string digits = trimmed[index..];
        return digits.Length > 0 && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int depth)
            ? depth
            : pixFmt.Contains("10") ? 10
            : pixFmt.Contains("12") ? 12
            : pixFmt.Contains("16") ? 16
            : 8;
    }

    static double ParseFraction(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        int slash = value.IndexOf('/');
        if (slash < 0)
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double single) ? single : 0;

        if (double.TryParse(value[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
            double.TryParse(value[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
            denominator != 0)
        {
            return numerator / denominator;
        }
        return 0;
    }

    static bool GetDisposition(JsonElement stream, string name)
    {
        return stream.TryGetProperty("disposition", out JsonElement disposition) &&
               disposition.ValueKind == JsonValueKind.Object &&
               disposition.TryGetProperty(name, out JsonElement value) &&
               (value.ValueKind == JsonValueKind.True ||
                (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int i) && i != 0));
    }

    static string GetString(JsonElement element, string property)
        => GetString(element, property, null);

    static string GetString(JsonElement element, string property, string? nested)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement value))
            return "";

        if (nested is not null)
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(nested, out JsonElement inner)
                ? inner.ToString()
                : "";

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    static long GetLong(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out long number) ? number : (long)value.GetDouble(),
            JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0,
            _ => 0
        };
    }

    static double GetDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0,
            _ => 0
        };
    }
}
