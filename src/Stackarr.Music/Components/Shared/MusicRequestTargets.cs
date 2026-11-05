using Stackarr.Core.Enums;
using Stackarr.Core.Requests;
using Stackarr.Music.Models;
using Stackarr.Requests;

namespace Stackarr.Music.Components.Shared;

/// <summary>
/// Builds a <see cref="RequestTarget"/> from a music catalog item. Kept out of the Blazor
/// components so the mapping is unit-testable.
/// </summary>
public static class MusicRequestTargets
{
    public static RequestTarget ForArtist(MusicItem item) => new(
        MusicMediaDomains.Music,
        RequestKind.Artist,
        ProviderIds(item, includeAlbum: false),
        item.Title,
        item.Year > 0 ? item.Year : null,
        PosterUrl: item.ArtUrl);

    public static RequestTarget ForAlbum(MusicItem item) => new(
        MusicMediaDomains.Music,
        RequestKind.Album,
        ProviderIds(item, includeAlbum: true),
        item.Title,
        item.Year > 0 ? item.Year : null,
        PosterUrl: item.ArtUrl);

    public static RequestTarget ForTrack(MusicItem item) => new(
        MusicMediaDomains.Music,
        RequestKind.Track,
        ProviderIds(item, includeAlbum: true),
        item.Title,
        item.Year > 0 ? item.Year : null,
        PosterUrl: item.ArtUrl);

    static Dictionary<string, string> ProviderIds(MusicItem item, bool includeAlbum)
    {
        long artistDeezerId = item.EntityType == MusicEntityType.Artist ? item.DeezerId : item.ArtistDeezerId;
        return ProviderIdMap.Build(
            (MusicProviderIds.DeezerArtist, artistDeezerId > 0 ? artistDeezerId.ToString() : null),
            (MusicProviderIds.Deezer, includeAlbum && item.DeezerId > 0 ? item.DeezerId.ToString() : null));
    }
}
