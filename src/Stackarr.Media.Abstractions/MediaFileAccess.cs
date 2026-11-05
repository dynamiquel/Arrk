namespace Stackarr.Media.Abstractions;

/// <summary>
/// Translates a path reported by a library manager (which sees media at its own container mount,
/// e.g. <c>/movies</c> in Radarr) into a path this Stackarr process can reach.
/// </summary>
public interface IMediaPathMapper
{
    /// <summary>Returns the local path, or <c>null</c> when no mapping applies.</summary>
    string? MapToLocalPath(string arrPath);
}

/// <summary>Scans a media file on disk for detailed stream metadata.</summary>
public interface IMediaProbe
{
    /// <summary>
    /// Scans <paramref name="path"/>. Returns <c>null</c> when the file is unreachable or no
    /// probe tool is available, so callers can fall back to library metadata.
    /// </summary>
    Task<MediaTechnicalInfo?> ProbeAsync(string path, CancellationToken ct = default);
}
