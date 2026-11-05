namespace Stackarr.Music.Services;

/// <summary>
/// One row in a playlist popup. A song is playable from the library when it has a Navidrome song id
/// (<see cref="SongId"/>); otherwise the row is a Deezer preview (<see cref="PreviewUrl"/>) for a song
/// we don't hold. The download fields carry the in-flight grab's progress for a song still coming
/// down (matched by its Deezer track id), so the row can show a progress bar.
/// </summary>
public sealed record PlaylistTrack(
    string Title,
    string Artist,
    string Album,
    string SongId,
    string PreviewUrl,
    long AlbumDeezerId,
    long ArtistDeezerId,
    string CoverUrl,
    long DeezerTrackId = 0,
    bool IsDownloading = false,
    long DownloadSize = 0,
    long DownloadSizeLeft = 0)
{
    public bool InLibrary => !string.IsNullOrEmpty(SongId);
    public bool Playable => InLibrary || !string.IsNullOrEmpty(PreviewUrl);

    public double DownloadPercent => DownloadSize > 0
        ? Math.Clamp((DownloadSize - DownloadSizeLeft) / (double)DownloadSize * 100, 0, 100)
        : 0;

    /// <summary>"Downloading" | "Missing" for a song we don't hold yet.</summary>
    public string MissingStatus => IsDownloading ? "Downloading" : "Missing";
}

/// <summary>
/// Mini-player queue rules for playlists. A 30-second Deezer preview for a song we do not hold never
/// enters an album or playlist queue; otherwise advancing would play a preview the user did not ask
/// for. A preview plays only when the user picks that row, and then as a one-off so previous and next
/// never reach it.
/// </summary>
public static class PlaylistPlayback
{
    /// <summary>The songs we actually hold, in order, the album/playlist queue.</summary>
    public static List<PlaylistTrack> LibraryQueue(IReadOnlyList<PlaylistTrack> tracks) =>
        [.. tracks.Where(t => t.InLibrary)];

    /// <summary>
    /// The queue and start index for a row the user explicitly clicked. A song we hold starts the
    /// library queue at its position; a preview becomes a one-off queue (never part of prev/next).
    /// </summary>
    public static (List<PlaylistTrack> Queue, int Index) ForTrack(
        IReadOnlyList<PlaylistTrack> tracks, PlaylistTrack track)
    {
        if (!track.InLibrary)
            return (!string.IsNullOrEmpty(track.PreviewUrl) ? [track] : [], 0);

        List<PlaylistTrack> queue = LibraryQueue(tracks);
        int index = queue.FindIndex(t => ReferenceEquals(t, track));
        return (queue, index < 0 ? 0 : index);
    }
}
