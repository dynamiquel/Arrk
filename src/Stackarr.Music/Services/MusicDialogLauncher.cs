using MudBlazor;
using Stackarr.Music.Components.Pages;
using Stackarr.Music.Models;
using Stackarr.Navidrome;
using Stackarr.Navidrome.Models;

namespace Stackarr.Music.Services;

/// <summary>
/// Opens the right music popup for a <see cref="MusicItem"/>, so discovery, the library and every
/// shelf share one routing rule: albums → album popup, artists → artist popup, playlists →
/// playlist popup, tracks → the album (or artist) they belong to. Kept out of the components so the
/// behaviour is defined once instead of being re-derived on every page.
/// </summary>
public sealed class MusicDialogLauncher(IDialogService dialogs)
{
    /// <summary>Expands a discovery track to its album (by Deezer id) or its artist, and opens that.</summary>
    public async Task<IDialogReference> OpenAsync(MusicItem item, bool isAdmin)
    {
        switch (item.EntityType)
        {
            case MusicEntityType.Album:
                return await OpenAlbumAsync(item, isAdmin);
            case MusicEntityType.Artist:
                return await OpenArtistAsync(item, isAdmin);
            case MusicEntityType.Playlist:
                return await OpenDeezerPlaylistAsync(item);
            default:
                if (item.AlbumDeezerId > 0)
                {
                    MusicItem album = new()
                    {
                        EntityType = MusicEntityType.Album,
                        Title = string.IsNullOrEmpty(item.AlbumName) ? item.Title : item.AlbumName,
                        ArtistName = item.ArtistName,
                        DeezerId = item.AlbumDeezerId,
                        ArtUrl = item.ArtUrl
                    };
                    return await OpenAlbumAsync(album, isAdmin);
                }
                MusicItem artist = new()
                {
                    EntityType = MusicEntityType.Artist,
                    Title = item.ArtistName,
                    DeezerId = item.ArtistDeezerId,
                    ArtUrl = item.ArtUrl
                };
                return await OpenArtistAsync(artist, isAdmin);
        }
    }

    public async Task<IDialogReference> OpenAlbumAsync(MusicItem album, bool isAdmin)
    {
        DialogParameters<AlbumDetailDialog> parameters = new()
        {
            { x => x.Album, album },
            { x => x.IsAdmin, isAdmin }
        };
        return await dialogs.ShowAsync<AlbumDetailDialog>(album.Title, parameters, Detail());
    }

    public async Task<IDialogReference> OpenArtistAsync(MusicItem artist, bool isAdmin)
    {
        DialogParameters<ArtistDetailDialog> parameters = new()
        {
            { x => x.Artist, artist },
            { x => x.IsAdmin, isAdmin }
        };
        return await dialogs.ShowAsync<ArtistDetailDialog>(artist.Title, parameters, Detail());
    }

    public async Task<IDialogReference> OpenDeezerPlaylistAsync(MusicItem playlist)
    {
        DialogParameters<PlaylistDetailDialog> parameters = new() { { x => x.Playlist, playlist } };
        return await dialogs.ShowAsync<PlaylistDetailDialog>(playlist.Title, parameters, Detail());
    }

    public async Task<IDialogReference> OpenLibraryPlaylistAsync(SubsonicPlaylist playlist)
    {
        MusicItem item = new()
        {
            EntityType = MusicEntityType.Playlist,
            Title = playlist.Name,
            ArtUrl = string.IsNullOrEmpty(playlist.CoverArt) ? "" : NavidromeService.CoverArtUrl(playlist.CoverArt)
        };
        DialogParameters<PlaylistDetailDialog> parameters = new()
        {
            { x => x.Playlist, item },
            { x => x.NavidromeId, playlist.Id }
        };
        return await dialogs.ShowAsync<PlaylistDetailDialog>(playlist.Name, parameters, Detail());
    }

    public async Task<IDialogReference> OpenSubscriptionAsync(MusicSubscription subscription)
    {
        MusicItem item = new()
        {
            EntityType = MusicEntityType.Playlist,
            Title = subscription.Name,
            ArtUrl = subscription.ArtUrl
        };
        DialogParameters<PlaylistDetailDialog> parameters = new()
        {
            { x => x.Playlist, item },
            { x => x.Subscription, subscription }
        };
        return await dialogs.ShowAsync<PlaylistDetailDialog>(subscription.Name, parameters, Detail());
    }

    /// <summary>The shared detail-popup geometry (matches movie/TV dialogs). A factory, not a
    /// shared instance, so a dialog can never mutate geometry for the next one.</summary>
    public static DialogOptions Detail() => new()
    {
        FullWidth = true,
        MaxWidth = MaxWidth.Medium,
        CloseOnEscapeKey = true,
        Position = DialogPosition.Center
    };
}
