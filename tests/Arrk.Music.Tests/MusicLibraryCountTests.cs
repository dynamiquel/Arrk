using Arrk.Core.Models;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// Verifies that per-track placement (what the importer recorded) is what the library reports, so the
/// Songs/Downloaded/Missing tiles and the album counts all agree.
/// </summary>
public class MusicLibraryCountTests
{
    [Test]
    public async Task GetTracks_ReportsPlacedTracks_OnAPartialAlbum()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        int albumId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Ella Langley", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Dandelion", DeezerId = 123, TotalTrackCount = 3, Monitored = true };
            album.Tracks.Add(new MusicTrack { Title = "One", TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true, FilePath = "Ella Langley/Dandelion/01 - One.flac" });
            album.Tracks.Add(new MusicTrack { Title = "Two", TrackNumber = 2, AbsoluteTrackNumber = 2, HasFile = false });
            album.Tracks.Add(new MusicTrack { Title = "Three", TrackNumber = 3, AbsoluteTrackNumber = 3, HasFile = false });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            albumId = album.Id;
        });

        List<Track> tracks = await library.GetTracksAsync(albumId);

        // Only track 1 is placed; the album is partial so availability can't be the source of truth.
        await Assert.That(tracks.Count(t => t.HasFile)).IsEqualTo(1);
        await Assert.That(tracks.Single(t => t.HasFile).Title).IsEqualTo("One");
    }

    [Test]
    public async Task AlbumCount_UsesPlacedTracks_NotAStaleNavidromeCount()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Ella Langley", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Dandelion", DeezerId = 123, TotalTrackCount = 19, Monitored = true };
            album.Tracks.Add(new MusicTrack { Title = "One", TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true, FilePath = "x/01.flac" });
            album.Tracks.Add(new MusicTrack { Title = "Two", TrackNumber = 2, AbsoluteTrackNumber = 2, HasFile = false });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
        });

        List<Album> albums = await library.GetAllAlbumsAsync();

        await Assert.That(albums).HasCount(1);
        await Assert.That(albums[0].TrackFileCount).IsEqualTo(1);
    }
}
