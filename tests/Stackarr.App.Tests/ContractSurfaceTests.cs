namespace Stackarr.App.Tests;

/// <summary>
/// Pins the contract surface plugins build against: concrete implementations must satisfy the
/// capability interfaces their consumers use, and the domain models must compose the shared media
/// capabilities instead of forcing one god interface.
/// </summary>
public class ContractSurfaceTests
{
    [Test]
    public async Task TvModels_ComposeSharedMediaCapabilities()
    {
        await Assert.That(typeof(Stackarr.Media.Abstractions.IMediaDescriptor).IsAssignableFrom(typeof(Stackarr.Tv.Abstractions.TvMovie))).IsTrue();
        await Assert.That(typeof(Stackarr.Media.Abstractions.IMediaArtwork).IsAssignableFrom(typeof(Stackarr.Tv.Abstractions.TvMovie))).IsTrue();
        await Assert.That(typeof(Stackarr.Media.Abstractions.IMediaLibraryState).IsAssignableFrom(typeof(Stackarr.Tv.Abstractions.TvMovie))).IsTrue();
        await Assert.That(typeof(Stackarr.Tv.Abstractions.ITmdbIdentity).IsAssignableFrom(typeof(Stackarr.Tv.Abstractions.TvMovie))).IsTrue();
        await Assert.That(typeof(Stackarr.Tv.Abstractions.ITvdbIdentity).IsAssignableFrom(typeof(Stackarr.Tv.Abstractions.TvSeries))).IsTrue();
        await Assert.That(typeof(Stackarr.Tv.Abstractions.IImdbIdentity).IsAssignableFrom(typeof(Stackarr.Tv.Abstractions.TvSeries))).IsTrue();
    }

    [Test]
    public async Task MusicModels_ComposeSharedMediaCapabilities()
    {
        await Assert.That(typeof(Stackarr.Media.Abstractions.IMediaDescriptor).IsAssignableFrom(typeof(Stackarr.Music.Abstractions.Artist))).IsTrue();
        await Assert.That(typeof(Stackarr.Media.Abstractions.IMediaArtwork).IsAssignableFrom(typeof(Stackarr.Music.Abstractions.Album))).IsTrue();
        await Assert.That(typeof(Stackarr.Music.Abstractions.IDeezerIdentity).IsAssignableFrom(typeof(Stackarr.Music.Abstractions.Track))).IsTrue();
    }

    [Test]
    public async Task ViewModels_ExposeDownloadAndWatchCapabilities()
    {
        await Assert.That(typeof(Stackarr.Media.Abstractions.IMediaDownloadState).IsAssignableFrom(typeof(Stackarr.Tvarr.Components.Shared.MediaItem))).IsTrue();
        await Assert.That(typeof(Stackarr.Media.Abstractions.IMediaWatchState).IsAssignableFrom(typeof(Stackarr.Tvarr.Components.Shared.MediaItem))).IsTrue();
        await Assert.That(typeof(Stackarr.Media.Abstractions.IMediaDownloadState).IsAssignableFrom(typeof(Stackarr.Music.Models.MusicItem))).IsTrue();
    }

    [Test]
    public async Task Implementations_SatisfyTheContractsOtherModulesUse()
    {
        await Assert.That(typeof(Stackarr.Tv.Abstractions.IMovieLibrary).IsAssignableFrom(typeof(Stackarr.Tvarr.Services.RadarrService))).IsTrue();
        await Assert.That(typeof(Stackarr.Tv.Abstractions.ISeriesLibrary).IsAssignableFrom(typeof(Stackarr.Tvarr.Services.SonarrService))).IsTrue();
        await Assert.That(typeof(Stackarr.Tv.Abstractions.ITvCatalog).IsAssignableFrom(typeof(Stackarr.Tvarr.Services.MediaCatalogService))).IsTrue();
        await Assert.That(typeof(Stackarr.Tv.Abstractions.ITvPlayerLinks).IsAssignableFrom(typeof(Stackarr.Jellyfin.JellyfinLinkService))).IsTrue();
        await Assert.That(typeof(Stackarr.Music.Abstractions.IMusicLibrary).IsAssignableFrom(typeof(Stackarr.Music.Services.MusicLibrary))).IsTrue();
        await Assert.That(typeof(Stackarr.Downloads.Abstractions.ITorrentDownloader).IsAssignableFrom(typeof(Stackarr.Transmission.TransmissionService))).IsTrue();
    }

    [Test]
    public async Task Descriptor_CarriesTheUnifiedReleaseDate()
    {
        DateTime date = new(2026, 1, 2);
        Stackarr.Media.Abstractions.IMediaDescriptor movie = new Stackarr.Tv.Abstractions.TvMovie { ReleaseDate = date };
        Stackarr.Media.Abstractions.IMediaDescriptor series = new Stackarr.Tv.Abstractions.TvSeries { FirstAired = date };
        Stackarr.Media.Abstractions.IMediaDescriptor episode = new Stackarr.Tv.Abstractions.TvEpisode { AirDate = date };
        Stackarr.Media.Abstractions.IMediaDescriptor album = new Stackarr.Music.Abstractions.Album { ReleaseDate = date };

        await Assert.That(movie.ReleaseDate).IsEqualTo(date);
        await Assert.That(series.ReleaseDate).IsEqualTo(date);
        await Assert.That(episode.ReleaseDate).IsEqualTo(date);
        await Assert.That(album.ReleaseDate).IsEqualTo(date);
    }

}
