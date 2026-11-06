using Arrk.Media.Abstractions;

namespace Arrk.Downloads.Abstractions;

/// <summary>
/// Maps a download (usually a torrent hash) to the media entity it belongs to. Download
/// management owns the torrent lifecycle but not media identity; library modules contribute a
/// resolver so download events can be targeted at the users following that title.
/// </summary>
public interface IMediaDownloadEntityResolver
{
    /// <summary>Returns the media entity for a hash, or null when this module does not know it.</summary>
    Task<MediaEntityRef?> ResolveAsync(string torrentHash, CancellationToken ct = default);
}
