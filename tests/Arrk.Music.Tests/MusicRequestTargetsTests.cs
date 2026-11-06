using Arrk.Requests;
using Arrk.Music.Components.Shared;
using Arrk.Ui.Components;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.Core.Enums;
using Arrk.Core.Requests;

namespace Arrk.Music.Tests;

public class MusicRequestTargetsTests
{
    static MusicItem Artist => new()
    {
        EntityType = MusicEntityType.Artist,
        Title = "Radiohead",
        DeezerId = 399
    };

    static MusicItem Album => new()
    {
        EntityType = MusicEntityType.Album,
        Title = "In Rainbows",
        ArtistName = "Radiohead",
        DeezerId = 1234,
        ArtistDeezerId = 399
    };

    [Test]
    public async Task ForArtist_CarriesArtistDeezerId()
    {
        RequestTarget target = MusicRequestTargets.ForArtist(Artist);

        await Assert.That(target.Domain).IsEqualTo(MusicMediaDomains.Music);
        await Assert.That(target.Kind).IsEqualTo(RequestKind.Artist);
        await Assert.That(target.ProviderIds[MusicProviderIds.DeezerArtist]).IsEqualTo("399");
        await Assert.That(target.ProviderIds.ContainsKey(MusicProviderIds.Deezer)).IsFalse();
    }

    [Test]
    public async Task ForAlbum_CarriesBothAlbumAndArtistDeezerIds()
    {
        RequestTarget target = MusicRequestTargets.ForAlbum(Album);

        await Assert.That(target.Kind).IsEqualTo(RequestKind.Album);
        await Assert.That(target.ProviderIds[MusicProviderIds.Deezer]).IsEqualTo("1234");
        await Assert.That(target.ProviderIds[MusicProviderIds.DeezerArtist]).IsEqualTo("399");
    }

    [Test]
    public async Task ForTrack_UsesAlbumKindIds()
    {
        RequestTarget target = MusicRequestTargets.ForTrack(Album with { EntityType = MusicEntityType.Track });

        await Assert.That(target.Kind).IsEqualTo(RequestKind.Track);
        await Assert.That(target.ProviderIds[MusicProviderIds.Deezer]).IsEqualTo("1234");
    }

    [Test]
    public async Task ForAlbum_WithoutDeezerId_CarriesNoProviderIds()
    {
        RequestTarget target = MusicRequestTargets.ForAlbum(Album with { DeezerId = 0, ArtistDeezerId = 0 });

        await Assert.That(target.ProviderIds).IsEmpty();
    }
}
