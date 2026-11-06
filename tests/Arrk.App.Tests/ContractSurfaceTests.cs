namespace Arrk.App.Tests;

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
        await Assert.That(typeof(Arrk.Media.Abstractions.IMediaDescriptor).IsAssignableFrom(typeof(Arrk.Tv.Abstractions.TvMovie))).IsTrue();
        await Assert.That(typeof(Arrk.Media.Abstractions.IMediaArtwork).IsAssignableFrom(typeof(Arrk.Tv.Abstractions.TvMovie))).IsTrue();
        await Assert.That(typeof(Arrk.Media.Abstractions.IMediaLibraryState).IsAssignableFrom(typeof(Arrk.Tv.Abstractions.TvMovie))).IsTrue();
        await Assert.That(typeof(Arrk.Tv.Abstractions.ITmdbIdentity).IsAssignableFrom(typeof(Arrk.Tv.Abstractions.TvMovie))).IsTrue();
        await Assert.That(typeof(Arrk.Tv.Abstractions.ITvdbIdentity).IsAssignableFrom(typeof(Arrk.Tv.Abstractions.TvSeries))).IsTrue();
        await Assert.That(typeof(Arrk.Tv.Abstractions.IImdbIdentity).IsAssignableFrom(typeof(Arrk.Tv.Abstractions.TvSeries))).IsTrue();
    }

    [Test]
    public async Task MusicModels_ComposeSharedMediaCapabilities()
    {
        await Assert.That(typeof(Arrk.Media.Abstractions.IMediaDescriptor).IsAssignableFrom(typeof(Arrk.Music.Abstractions.Artist))).IsTrue();
        await Assert.That(typeof(Arrk.Media.Abstractions.IMediaArtwork).IsAssignableFrom(typeof(Arrk.Music.Abstractions.Album))).IsTrue();
        await Assert.That(typeof(Arrk.Music.Abstractions.IDeezerIdentity).IsAssignableFrom(typeof(Arrk.Music.Abstractions.Track))).IsTrue();
    }

    [Test]
    public async Task ViewModels_ExposeDownloadAndWatchCapabilities()
    {
        await Assert.That(typeof(Arrk.Media.Abstractions.IMediaDownloadState).IsAssignableFrom(typeof(Arrk.Tvarr.Components.Shared.MediaItem))).IsTrue();
        await Assert.That(typeof(Arrk.Media.Abstractions.IMediaWatchState).IsAssignableFrom(typeof(Arrk.Tvarr.Components.Shared.MediaItem))).IsTrue();
        await Assert.That(typeof(Arrk.Media.Abstractions.IMediaDownloadState).IsAssignableFrom(typeof(Arrk.Music.Models.MusicItem))).IsTrue();
    }

    [Test]
    public async Task Implementations_SatisfyTheContractsOtherModulesUse()
    {
        await Assert.That(typeof(Arrk.Tv.Abstractions.IMovieLibrary).IsAssignableFrom(typeof(Arrk.Tvarr.Services.RadarrService))).IsTrue();
        await Assert.That(typeof(Arrk.Tv.Abstractions.ISeriesLibrary).IsAssignableFrom(typeof(Arrk.Tvarr.Services.SonarrService))).IsTrue();
        await Assert.That(typeof(Arrk.Tv.Abstractions.ITvCatalog).IsAssignableFrom(typeof(Arrk.Tvarr.Services.MediaCatalogService))).IsTrue();
        await Assert.That(typeof(Arrk.Tv.Abstractions.ITvPlayerLinks).IsAssignableFrom(typeof(Arrk.Jellyfin.JellyfinLinkService))).IsTrue();
        await Assert.That(typeof(Arrk.Music.Abstractions.IMusicLibrary).IsAssignableFrom(typeof(Arrk.Music.Services.MusicLibrary))).IsTrue();
        await Assert.That(typeof(Arrk.Downloads.Abstractions.ITorrentDownloader).IsAssignableFrom(typeof(Arrk.Transmission.TransmissionService))).IsTrue();
    }

    [Test]
    public async Task Descriptor_CarriesTheUnifiedReleaseDate()
    {
        DateTime date = new(2026, 1, 2);
        Arrk.Media.Abstractions.IMediaDescriptor movie = new Arrk.Tv.Abstractions.TvMovie { ReleaseDate = date };
        Arrk.Media.Abstractions.IMediaDescriptor series = new Arrk.Tv.Abstractions.TvSeries { FirstAired = date };
        Arrk.Media.Abstractions.IMediaDescriptor episode = new Arrk.Tv.Abstractions.TvEpisode { AirDate = date };
        Arrk.Media.Abstractions.IMediaDescriptor album = new Arrk.Music.Abstractions.Album { ReleaseDate = date };

        await Assert.That(movie.ReleaseDate).IsEqualTo(date);
        await Assert.That(series.ReleaseDate).IsEqualTo(date);
        await Assert.That(episode.ReleaseDate).IsEqualTo(date);
        await Assert.That(album.ReleaseDate).IsEqualTo(date);
    }

}
