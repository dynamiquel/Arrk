using Stackarr.Core.Models;

namespace Stackarr.Tvarr.Models;

/// <summary>
/// Converts the arr file resources (movie/episode <c>mediaInfo</c> + <c>quality</c>) into the
/// shared <see cref="MediaTechnicalInfo"/> / <see cref="QualityInfo"/> models used by the UI.
/// The arrs provide a good baseline; an ffprobe scan can later fill the remaining fields.
/// </summary>
public static class MediaInfoMapper
{
    public static QualityInfo QualityFrom(FileQualityDto? fileQuality, MediaInfoDto? mediaInfo)
    {
        string name = fileQuality?.Quality?.Name ?? "";
        string source = fileQuality?.Quality?.Source ?? "";

        string resolution = ResolutionFromQualityInt(fileQuality?.Quality?.Resolution ?? 0);
        if (resolution.Length == 0)
            resolution = MediaFormatSummary.ResolutionFromHeight(ParseDimensions(mediaInfo?.Resolution).Height);

        string hdrType = $"{mediaInfo?.VideoDynamicRange} {mediaInfo?.VideoDynamicRangeType}".Trim();

        return ArrQualityMapper.FromRadarrQuality(
            name,
            resolution,
            mediaInfo?.VideoCodec ?? "",
            mediaInfo?.AudioCodec ?? "",
            hdrType,
            source);
    }

    public static MediaTechnicalInfo TechnicalFrom(MediaInfoDto? mediaInfo, string path, long sizeBytes, DateTime? dateAdded)
    {
        string fileName = Path.GetFileName(path);
        if (mediaInfo is null)
        {
            return new MediaTechnicalInfo
            {
                FileName = fileName,
                FilePath = path,
                SizeBytes = sizeBytes
            };
        }

        (int width, int height) = ParseDimensions(mediaInfo.Resolution);
        (string hdrText, MediaDynamicRange hdrRange) = MediaFormatSummary.Hdr(
            mediaInfo.VideoDynamicRange,
            mediaInfo.VideoDynamicRangeType);

        VideoStreamInfo? video = width > 0 || height > 0 || mediaInfo.VideoCodec.Length > 0
            ? new VideoStreamInfo
            {
                Codec = mediaInfo.VideoCodec,
                CodecDisplay = MediaFormatSummary.FriendlyVideoCodec(mediaInfo.VideoCodec),
                BitDepth = mediaInfo.VideoBitDepth,
                Width = width,
                Height = height,
                Resolution = mediaInfo.Resolution,
                AspectRatio = MediaFormatSummary.AspectRatio(width, height),
                FrameRate = mediaInfo.VideoFps,
                Bitrate = mediaInfo.VideoBitrate,
                DynamicRange = hdrRange,
                HdrFormat = hdrText,
                ScanType = mediaInfo.ScanType
            }
            : null;

        List<AudioStreamInfo> audio = [];
        if (mediaInfo.AudioCodec.Length > 0 || mediaInfo.AudioChannels > 0)
        {
            List<string> languages = MediaFormatSummary.LanguageNamesFromList(mediaInfo.AudioLanguages);
            audio.Add(new AudioStreamInfo
            {
                Index = mediaInfo.AudioStreamCount > 1 ? 1 : 0,
                Codec = mediaInfo.AudioCodec,
                Channels = mediaInfo.AudioChannels,
                Bitrate = mediaInfo.AudioBitrate,
                Language = mediaInfo.AudioLanguages.Split(['/', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "",
                LanguageName = string.Join(", ", languages),
                Format = MediaFormatSummary.FriendlyAudio(mediaInfo.AudioCodec, mediaInfo.AudioChannels),
                IsDefault = true
            });
        }

        List<SubtitleStreamInfo> subtitles = MediaFormatSummary
            .LanguageNamesFromList(mediaInfo.Subtitles)
            .Select(name => new SubtitleStreamInfo { LanguageName = name })
            .ToList();

        return new MediaTechnicalInfo
        {
            FileName = fileName,
            FilePath = path,
            SizeBytes = sizeBytes,
            Duration = mediaInfo.RunTime,
            OverallBitrate = mediaInfo.VideoBitrate + mediaInfo.AudioBitrate,
            Video = video,
            Audio = audio,
            Subtitles = subtitles
        };
    }

    static (int Width, int Height) ParseDimensions(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return (0, 0);

        string[] parts = resolution.Split(['x', 'X', '*'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return (0, 0);

        int.TryParse(parts[0], out int width);
        int.TryParse(parts[1], out int height);
        return (width, height);
    }

    static string ResolutionFromQualityInt(int resolution) => resolution switch
    {
        >= 2000 => Resolutions.R2160p,
        >= 1000 => Resolutions.R1080p,
        >= 700 => Resolutions.R720p,
        >= 400 => Resolutions.R480p,
        _ => ""
    };
}
