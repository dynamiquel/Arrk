using Microsoft.EntityFrameworkCore;
using Arrk.Core;
using Arrk.Core.Models;
using Arrk.Music.Models;
using Arrk.Music.Services;

namespace Arrk.Music.Tests;

/// <summary>Covers the library repair pass: merging albums that were added twice, removing
/// exactly-duplicated track rows, and backfilling legacy rows.</summary>
public class MusicLibraryRepairTests
{
    static (MusicLibrary Library, TestMusicDb Db) Build()
    {
        TestMusicDb db = new();
        return (MusicTestHelpers.Library(db), db);
    }

    [Test]
    public async Task MergeDuplicateAlbums_MergesSameDeezerId_AndKeepsTheFileBearingTrack()
    {
        (MusicLibrary library, TestMusicDb db) = Build();
        using (db)
        {
            db.Seed(d =>
            {
                MusicArtist artist = new() { Name = "Daft Punk", Added = DateTime.UtcNow };
                MusicAlbum first = new() { Title = "Discovery", DeezerId = 123, TotalTrackCount = 1, Added = DateTime.UtcNow };
                first.Tracks.Add(new MusicTrack { Title = "One More Time", TrackNumber = 1, AbsoluteTrackNumber = 1 });
                MusicAlbum second = new() { Title = "Discovery", DeezerId = 123, TotalTrackCount = 1, Added = DateTime.UtcNow };
                second.Tracks.Add(new MusicTrack { Title = "One More Time", TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true, FilePath = "Daft Punk - Discovery/01.flac" });
                artist.Albums.Add(first);
                artist.Albums.Add(second);
                d.Set<MusicArtist>().Add(artist);
                d.SaveChanges();
            });

            int merged = await library.MergeDuplicateAlbumsAsync();

            await Assert.That(merged).IsEqualTo(1);
            using ArrkDbContext ctx = db.CreateDbContext();
            await Assert.That(ctx.Set<MusicAlbum>().Count(a => a.DeezerId == 123)).IsEqualTo(1);
            MusicAlbum survivor = ctx.Set<MusicAlbum>().Include(a => a.Tracks).Single(a => a.DeezerId == 123);
            await Assert.That(survivor.Tracks.Count).IsEqualTo(1);
            await Assert.That(survivor.Tracks[0].HasFile).IsTrue();
            await Assert.That(survivor.Tracks[0].FilePath).IsEqualTo("Daft Punk - Discovery/01.flac");
        }
    }

    [Test]
    public async Task MergeDuplicateAlbums_MergesSameArtistAndTitle_WithoutDeezerId()
    {
        (MusicLibrary library, TestMusicDb db) = Build();
        using (db)
        {
            db.Seed(d =>
            {
                MusicArtist artist = new() { Name = "Sade", Added = DateTime.UtcNow };
                artist.Albums.Add(new MusicAlbum { Title = "The Ultimate Collection", DeezerId = 0, Added = DateTime.UtcNow });
                artist.Albums.Add(new MusicAlbum { Title = "The Ultimate Collection", DeezerId = 0, Added = DateTime.UtcNow });
                d.Set<MusicArtist>().Add(artist);
                d.SaveChanges();
            });

            int merged = await library.MergeDuplicateAlbumsAsync();

            await Assert.That(merged).IsEqualTo(1);
            using ArrkDbContext ctx = db.CreateDbContext();
            await Assert.That(ctx.Set<MusicAlbum>().Count(a => a.Title == "The Ultimate Collection")).IsEqualTo(1);
        }
    }

    [Test]
    public async Task RemoveDuplicateTracks_RemovesExactDupes_AndKeepsTheFile()
    {
        (MusicLibrary library, TestMusicDb db) = Build();
        using (db)
        {
            int albumId = 0;
            db.Seed(d =>
            {
                MusicArtist artist = new() { Name = "Noah Kahan", Added = DateTime.UtcNow };
                MusicAlbum album = new() { Title = "Stick Season", Added = DateTime.UtcNow };
                album.Tracks.Add(new MusicTrack { Title = "Dial Drunk", DiscNumber = 1, TrackNumber = 1, AbsoluteTrackNumber = 1 });
                album.Tracks.Add(new MusicTrack { Title = "Dial Drunk", DiscNumber = 1, TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true, FilePath = "Noah/01.flac" });
                // A genuine deluxe repeat (different number) must be kept.
                album.Tracks.Add(new MusicTrack { Title = "Dial Drunk", DiscNumber = 1, TrackNumber = 20, AbsoluteTrackNumber = 20 });
                artist.Albums.Add(album);
                d.Set<MusicArtist>().Add(artist);
                d.SaveChanges();
                albumId = album.Id;
            });

            int removed = await library.RemoveDuplicateTracksAsync();

            await Assert.That(removed).IsEqualTo(1);
            using ArrkDbContext ctx = db.CreateDbContext();
            List<MusicTrack> tracks = ctx.Set<MusicTrack>().Where(t => t.AlbumId == albumId).ToList();
            await Assert.That(tracks.Count).IsEqualTo(2);
            await Assert.That(tracks.Any(t => t.HasFile)).IsTrue();
        }
    }

    [Test]
    public async Task BackfillAlbumAdded_SetsAddedForLegacyRows()
    {
        (MusicLibrary library, TestMusicDb db) = Build();
        using (db)
        {
            db.Seed(d =>
            {
                MusicArtist artist = new() { Name = "Radiohead", Added = DateTime.UtcNow };
                artist.Albums.Add(new MusicAlbum { Title = "In Rainbows", Added = default });
                d.Set<MusicArtist>().Add(artist);
                d.SaveChanges();
            });

            int backfilled = await library.BackfillAlbumAddedAsync();

            await Assert.That(backfilled).IsEqualTo(1);
            using ArrkDbContext ctx = db.CreateDbContext();
            await Assert.That(ctx.Set<MusicAlbum>().Single().Added.Year).IsGreaterThan(2000);
        }
    }

    [Test]
    public async Task SetTrackDeezerIds_FillsMissingIdsOnly()
    {
        (MusicLibrary library, TestMusicDb db) = Build();
        using (db)
        {
            int albumId = 0;
            int trackId = 0;
            db.Seed(d =>
            {
                MusicArtist artist = new() { Name = "Daft Punk", Added = DateTime.UtcNow };
                MusicAlbum album = new() { Title = "Discovery", DeezerId = 123, Added = DateTime.UtcNow };
                MusicTrack track = new() { Title = "One More Time", TrackNumber = 1, AbsoluteTrackNumber = 1 };
                album.Tracks.Add(track);
                artist.Albums.Add(album);
                d.Set<MusicArtist>().Add(artist);
                d.SaveChanges();
                albumId = album.Id;
                trackId = track.Id;
            });

            int updated = await library.SetTrackDeezerIdsAsync(albumId, new Dictionary<int, long> { [trackId] = 1001 });

            await Assert.That(updated).IsEqualTo(1);
            using ArrkDbContext ctx = db.CreateDbContext();
            await Assert.That(ctx.Set<MusicTrack>().Single(t => t.Id == trackId).DeezerId).IsEqualTo(1001);
        }
    }

    [Test]
    public async Task UpgradeAlbum_RebindsIdentity_AndKeepsPlacedFiles()
    {
        (MusicLibrary library, TestMusicDb db) = Build();
        using (db)
        {
            int albumId = 0;
            db.Seed(d =>
            {
                MusicArtist artist = new() { Name = "CD1", Added = DateTime.UtcNow };
                MusicAlbum album = new() { Title = "CD1", DeezerId = 0, Added = DateTime.UtcNow };
                album.Tracks.Add(new MusicTrack { Title = "Mr. Blue Sky", DiscNumber = 1, TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true, FilePath = "CD1/01.flac" });
                artist.Albums.Add(album);
                d.Set<MusicArtist>().Add(artist);
                d.SaveChanges();
                albumId = album.Id;
            });

            MusicItem detail = new()
            {
                EntityType = MusicEntityType.Album,
                Title = "Out of the Blue",
                ArtistName = "Electric Light Orchestra",
                DeezerId = 77,
                ArtistDeezerId = 9,
                TrackCount = 2,
                Tracks =
                [
                    new MusicTrackItem { Number = 1, DiscNumber = 1, Title = "Mr. Blue Sky", DeezerId = 100 },
                    new MusicTrackItem { Number = 2, DiscNumber = 1, Title = "Turn to Stone", DeezerId = 101 }
                ]
            };

            Album? upgraded = await library.UpgradeAlbumAsync(albumId, detail);

            await Assert.That(upgraded).IsNotNull();
            await Assert.That(upgraded!.DeezerId).IsEqualTo(77);
            await Assert.That(upgraded.Title).IsEqualTo("Out of the Blue");
            await Assert.That(upgraded.ArtistName).IsEqualTo("Electric Light Orchestra");
            await Assert.That(upgraded.Tracks.Count).IsEqualTo(2);
            Track placed = upgraded.Tracks.Single(t => t.Title == "Mr. Blue Sky");
            await Assert.That(placed.HasFile).IsTrue();
            await Assert.That(placed.FilePath).IsEqualTo("CD1/01.flac");
            await Assert.That(placed.DeezerId).IsEqualTo(100);
        }
    }
}
