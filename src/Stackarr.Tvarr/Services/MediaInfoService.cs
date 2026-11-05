using Microsoft.Extensions.Logging;
using Stackarr.Core.Models;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// Resolves the full media info for a movie or episode: the arr's scanned metadata is the
/// baseline and, when the file is reachable from Stackarr, an ffprobe scan fills in the
/// stream-level detail. Results are returned as the shared <see cref="MediaTechnicalInfo"/>.
/// </summary>
public class MediaInfoService
{
    readonly RadarrClient _radarr;
    readonly SonarrClient _sonarr;
    readonly IMediaProbe _probe;
    readonly IMediaPathMapper _paths;
    readonly ILogger<MediaInfoService>? _logger;

    public MediaInfoService(
        RadarrClient radarr,
        SonarrClient sonarr,
        IMediaProbe probe,
        IMediaPathMapper paths,
        ILogger<MediaInfoService>? logger = null)
    {
        _radarr = radarr;
        _sonarr = sonarr;
        _probe = probe;
        _paths = paths;
        _logger = logger;
    }

    public async Task<MediaTechnicalInfo?> GetMovieMediaInfoAsync(int movieId, CancellationToken ct = default)
    {
        try
        {
            MovieDto? movie = await _radarr.GetMovie(movieId, ct);
            MovieFileDto? file = movie?.MovieFile ?? movie?.MovieFiles.FirstOrDefault();
            if (file is null)
                return null;

            MediaTechnicalInfo baseline = MediaInfoMapper.TechnicalFrom(file.MediaInfo, file.Path, file.Size, file.DateAdded);
            return await EnrichAsync(baseline, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load media info for movie {MovieId}", movieId);
            return null;
        }
    }

    public async Task<MediaTechnicalInfo?> GetEpisodeMediaInfoAsync(int episodeId, CancellationToken ct = default)
    {
        try
        {
            EpisodeDto? episode = await _sonarr.GetEpisode(episodeId, ct);
            if (episode is null || episode.EpisodeFileId <= 0)
                return null;

            EpisodeFileDto? file = await _sonarr.GetEpisodeFile(episode.EpisodeFileId, ct);
            if (file is null)
                return null;

            MediaTechnicalInfo baseline = MediaInfoMapper.TechnicalFrom(file.MediaInfo, file.Path, file.Size, file.DateAdded);
            return await EnrichAsync(baseline, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load media info for episode {EpisodeId}", episodeId);
            return null;
        }
    }

    async Task<MediaTechnicalInfo> EnrichAsync(MediaTechnicalInfo baseline, CancellationToken ct)
    {
        string? local = _paths.MapToLocalPath(baseline.FilePath);
        if (local is null)
            return baseline;

        MediaTechnicalInfo? probed = await _probe.ProbeAsync(local, ct);
        return probed is null ? baseline : Merge(baseline, probed);
    }

    static MediaTechnicalInfo Merge(MediaTechnicalInfo baseline, MediaTechnicalInfo probed) => new()
    {
        FileName = Prefer(baseline.FileName, probed.FileName),
        FilePath = Prefer(baseline.FilePath, probed.FilePath),
        SizeBytes = baseline.SizeBytes > 0 ? baseline.SizeBytes : probed.SizeBytes,
        Container = probed.Container,
        Duration = Prefer(probed.Duration, baseline.Duration),
        OverallBitrate = probed.OverallBitrate > 0 ? probed.OverallBitrate : baseline.OverallBitrate,
        Video = MergeVideo(baseline.Video, probed.Video),
        Audio = probed.Audio.Count > 0 ? probed.Audio : baseline.Audio,
        Subtitles = probed.Subtitles.Count > 0 ? probed.Subtitles : baseline.Subtitles,
        Probed = true
    };

    static VideoStreamInfo? MergeVideo(VideoStreamInfo? baseline, VideoStreamInfo? probed)
    {
        if (probed is null)
            return baseline;
        if (baseline is null)
            return probed;

        // Prefer the scan, but keep arr values for fields ffprobe does not provide.
        return new VideoStreamInfo
        {
            Codec = Prefer(probed.Codec, baseline.Codec),
            CodecDisplay = Prefer(probed.CodecDisplay, baseline.CodecDisplay),
            Profile = Prefer(probed.Profile, baseline.Profile),
            Level = Prefer(probed.Level, baseline.Level),
            BitDepth = probed.BitDepth > 0 ? probed.BitDepth : baseline.BitDepth,
            Width = probed.Width > 0 ? probed.Width : baseline.Width,
            Height = probed.Height > 0 ? probed.Height : baseline.Height,
            Resolution = Prefer(probed.Resolution, baseline.Resolution),
            AspectRatio = Prefer(probed.AspectRatio, baseline.AspectRatio),
            FrameRate = probed.FrameRate > 0 ? probed.FrameRate : baseline.FrameRate,
            Bitrate = probed.Bitrate > 0 ? probed.Bitrate : baseline.Bitrate,
            DynamicRange = probed.DynamicRange != MediaDynamicRange.Sdr ? probed.DynamicRange : baseline.DynamicRange,
            HdrFormat = probed.HdrFormat != "SDR" ? probed.HdrFormat : baseline.HdrFormat,
            ColorSpace = Prefer(probed.ColorSpace, baseline.ColorSpace),
            ColorPrimaries = Prefer(probed.ColorPrimaries, baseline.ColorPrimaries),
            ColorTransfer = Prefer(probed.ColorTransfer, baseline.ColorTransfer),
            ChromaSubsampling = Prefer(probed.ChromaSubsampling, baseline.ChromaSubsampling),
            ScanType = Prefer(probed.ScanType, baseline.ScanType)
        };
    }

    static string Prefer(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second ?? "";
}
