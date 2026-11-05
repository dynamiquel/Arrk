using Microsoft.Extensions.Logging;
using Stackarr.Core.Enums;
using Stackarr.Core.Lifecycle;
using Stackarr.Core.Models;
using Stackarr.Core.Playback;
using Stackarr.Music.Models;
using Stackarr.Navidrome;

namespace Stackarr.Music.Services;

/// <summary>
/// Exposes downloaded albums as lifecycle-managed items. Albums kept in any Navidrome playlist are
/// marked protected so the lifecycle module applies the longer keep threshold.
/// </summary>
public sealed class MusicManagedMediaProvider(
    MusicLibrary library,
    MusicCatalogService catalog,
    MusicAcquisitionService acquisition,
    NavidromeService navidrome,
    ILogger<MusicManagedMediaProvider> logger) : IManagedMediaProvider
{
    static readonly TimeSpan PlaylistCacheLifetime = TimeSpan.FromMinutes(5);

    readonly SemaphoreSlim _playlistGate = new(1, 1);
    HashSet<string>? _playlistAlbums;
    DateTime _playlistCachedAt = DateTime.MinValue;

    public string Key => WatchActivityServices.Music;

    public string DisplayName => "Music";

    public async Task<IReadOnlyList<ManagedMediaItem>> GetItemsAsync(CancellationToken ct = default)
    {
        List<Album> albums;
        try
        {
            albums = await library.GetAllAlbumsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list albums for staleness");
            return [];
        }

        HashSet<string> playlistAlbums = await GetPlaylistAlbumsAsync(ct);
        List<ManagedMediaItem> items = [];

        foreach (Album album in albums)
        {
            long size = CalculateSize(album);
            if (size <= 0)
                continue;

            string itemKey = MediaItemKeys.MusicAlbum(album.ArtistName, album.Title);
            if (string.IsNullOrEmpty(itemKey))
                continue;

            bool kept = playlistAlbums.Contains(AlbumKey(album.ArtistName, album.Title));
            items.Add(new ManagedMediaItem(
                Key,
                itemKey,
                MusicMediaDomains.Music,
                "album",
                album.Title,
                album.ArtistName,
                album.PosterUrl,
                size,
                NormalizeAdded(album.Added),
                kept,
                kept ? "Bookmarked" : null,
                album.Id.ToString()));
        }

        return items;
    }

    public async Task<ManagedMediaDeleteResult> DeleteAsync(ManagedMediaItem item, CancellationToken ct = default)
    {
        if (!int.TryParse(item.LibraryId, out int albumId) || albumId <= 0)
            return new ManagedMediaDeleteResult(false, 0, "The album id is missing.");

        try
        {
            Album? album = await library.GetAlbumAsync(albumId, ct);
            if (album is not null)
            {
                foreach (Track track in album.Tracks.Where(t => t.HasFile))
                    await catalog.DeleteTrackFileAsync(albumId, track.Id, ct);
            }

            await library.DeleteAlbumAsync(albumId, ct);
            await navidrome.StartScanAsync(ct);
            catalog.InvalidateLibraryCache();
            library.InvalidateAvailability();
            return new ManagedMediaDeleteResult(true, item.SizeOnDisk);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete stale album {Title}", item.Title);
            return new ManagedMediaDeleteResult(false, 0, ex.Message);
        }
    }

    long CalculateSize(Album album)
    {
        long total = 0;
        foreach (Track track in album.Tracks)
        {
            if (!track.HasFile || string.IsNullOrWhiteSpace(track.FilePath))
                continue;
            try
            {
                string full = Path.Combine(acquisition.LibraryRoot, track.FilePath);
                FileInfo info = new(full);
                if (info.Exists)
                    total += info.Length;
            }
            catch
            {
                /* unreadable file: still let the item through if other tracks are measurable */
            }
        }
        return total;
    }

    async Task<HashSet<string>> GetPlaylistAlbumsAsync(CancellationToken ct)
    {
        if (_playlistAlbums is not null && DateTime.UtcNow - _playlistCachedAt < PlaylistCacheLifetime)
            return _playlistAlbums;

        await _playlistGate.WaitAsync(ct);
        try
        {
            if (_playlistAlbums is not null && DateTime.UtcNow - _playlistCachedAt < PlaylistCacheLifetime)
                return _playlistAlbums;

            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (SubsonicPlaylist playlist in await navidrome.GetPlaylistsAsync(ct))
            {
                SubsonicPlaylist? full = await navidrome.GetPlaylistAsync(playlist.Id, ct);
                foreach (SubsonicSong song in full?.Tracks ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(song.Artist) && !string.IsNullOrWhiteSpace(song.Album))
                        keys.Add(AlbumKey(song.Artist, song.Album));
                }
            }
            _playlistAlbums = keys;
            _playlistCachedAt = DateTime.UtcNow;
            return keys;
        }
        finally
        {
            _playlistGate.Release();
        }
    }

    static string AlbumKey(string artist, string album) =>
        $"{MediaItemKeys.Normalize(artist)}|{MediaItemKeys.Normalize(album)}";

    static DateTime NormalizeAdded(DateTime added) =>
        added == default ? DateTime.UtcNow.AddDays(-1) : added.ToUniversalTime();
}
