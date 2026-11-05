using Microsoft.EntityFrameworkCore;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Music.Models;
using Stackarr.Music.Services;
using Stackarr.TestSupport;

namespace Stackarr.Music.Tests;

/// <summary>
/// Covers the monitoring rules that separate "I asked for this album" from "this album is only here
/// because I grabbed one of its songs": a whole-album add monitors every track, a single-song import
/// leaves the album unmonitored with just the requested song wanted, and the startup reconcile
/// untangles the albums that predate those rules. Missing is reserved for genuinely-wanted tracks.
/// </summary>
public class MusicMonitoringTests
{
    static MusicItem AlbumItem(string title, long deezerId, params (string Title, long DeezerId)[] tracks) => new()
    {
        EntityType = MusicEntityType.Album,
        Title = title,
        ArtistName = "Ella Langley",
        ArtistDeezerId = 9,
        DeezerId = deezerId,
        TrackCount = tracks.Length,
        Tracks = [.. tracks.Select((t, i) => new MusicTrackItem
        {
            Number = i + 1,
            DiscNumber = 1,
            Title = t.Title,
            DeezerId = t.DeezerId
        })]
    };

    [Test]
    public async Task SingleSongImport_LeavesTheAlbumUnmonitored_AndWantsOnlyThatSong()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        Album added = (await library.AddOrUpdateAlbumAsync(
            AlbumItem("Dandelion", 123, ("One", 111L), ("Two", 222L), ("Three", 333L)),
            monitored: false, monitoredTrackDeezerId: 222))!;

        Album full = (await library.GetAlbumAsync(added.Id))!;
        await Assert.That(full.Monitored).IsFalse();
        await Assert.That(full.Tracks.Single(t => t.Title == "Two").Monitored).IsTrue();
        await Assert.That(full.Tracks.Single(t => t.Title == "One").Monitored).IsFalse();
        await Assert.That(full.Tracks.Single(t => t.Title == "Three").Monitored).IsFalse();
    }

    [Test]
    public async Task WholeAlbumAdd_MonitorsEveryTrack()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        Album added = (await library.AddOrUpdateAlbumAsync(
            AlbumItem("Dandelion", 123, ("One", 111L), ("Two", 222L)), monitored: true))!;

        Album full = (await library.GetAlbumAsync(added.Id))!;
        await Assert.That(full.Monitored).IsTrue();
        await Assert.That(full.Tracks.All(t => t.Monitored)).IsTrue();
    }

    [Test]
    public async Task SingleSongImport_DoesNotUnmonitorAnExplicitlyAddedAlbum()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        MusicItem album = AlbumItem("Dandelion", 123, ("One", 111L), ("Two", 222L));
        Album added = (await library.AddOrUpdateAlbumAsync(album, monitored: true))!;

        // A later single-song import (playlist auto-download) must not relax the explicit add.
        await library.AddOrUpdateAlbumAsync(album, monitored: false, monitoredTrackDeezerId: 222);

        Album full = (await library.GetAlbumAsync(added.Id))!;
        await Assert.That(full.Monitored).IsTrue();
        await Assert.That(full.Tracks.All(t => t.Monitored)).IsTrue();
    }

    [Test]
    public async Task SetAlbumMonitored_KeepsTracksAndIntentsInStep()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        Album added = (await library.AddOrUpdateAlbumAsync(
            AlbumItem("Dandelion", 123, ("One", 111L), ("Two", 222L)), monitored: false))!;

        await library.SetAlbumMonitoredAsync(added.Id, true);
        await Assert.That((await library.GetAlbumAsync(added.Id))!.Tracks.All(t => t.Monitored)).IsTrue();
        using (StackarrDbContext ctx = db.CreateDbContext())
        {
            LibraryIntent intent = ctx.Set<LibraryIntent>().Single(i => i.Kind == LibraryIntentKind.Album && i.TargetId == added.Id);
            await Assert.That(intent.State).IsEqualTo(LibraryIntentState.Wanted);
        }

        await library.SetAlbumMonitoredAsync(added.Id, false);
        await Assert.That((await library.GetAlbumAsync(added.Id))!.Tracks.All(t => !t.Monitored)).IsTrue();
        using (StackarrDbContext ctx = db.CreateDbContext())
        {
            LibraryIntent intent = ctx.Set<LibraryIntent>().Single(i => i.Kind == LibraryIntentKind.Album && i.TargetId == added.Id);
            await Assert.That(intent.State).IsEqualTo(LibraryIntentState.Ignored);
        }
    }

    [Test]
    public async Task Reconcile_UnmonitorsASongOnlyAlbum_AndIsIdempotent()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        int albumId = SeedSongOnlyAlbum(db);

        int reconciled = await library.ReconcileImplicitAlbumMonitoringAsync();

        await Assert.That(reconciled).IsEqualTo(1);
        Album full = (await library.GetAlbumAsync(albumId))!;
        await Assert.That(full.Monitored).IsFalse();
        await Assert.That(full.Tracks.All(t => !t.Monitored)).IsTrue();
        await Assert.That(await library.ReconcileImplicitAlbumMonitoringAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task Reconcile_LeavesAnExplicitlyAddedAlbumMonitored()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        int albumId = SeedSongOnlyAlbum(db);
        db.Seed(d => d.Set<LibraryIntent>().Add(new LibraryIntent
        {
            Kind = LibraryIntentKind.Album,
            TargetId = albumId,
            State = LibraryIntentState.Wanted,
            Source = "deezer",
            RequestedAt = DateTime.UtcNow
        }));

        await Assert.That(await library.ReconcileImplicitAlbumMonitoringAsync()).IsEqualTo(0);
        await Assert.That((await library.GetAlbumAsync(albumId))!.Monitored).IsTrue();
    }

    [Test]
    public async Task Reconcile_LeavesAnAlbumWithNoSongGrabAlone()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        // Partial album, monitored, but no recorded song grab: not enough evidence to unmonitor it.
        int albumId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Ella Langley", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Dandelion", DeezerId = 123, TotalTrackCount = 3, Monitored = true, Added = DateTime.UtcNow };
            album.Tracks.Add(new MusicTrack { Title = "One", TrackNumber = 1, AbsoluteTrackNumber = 1, DeezerId = 111, HasFile = true, Monitored = true });
            album.Tracks.Add(new MusicTrack { Title = "Two", TrackNumber = 2, AbsoluteTrackNumber = 2, DeezerId = 222, Monitored = true });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            albumId = album.Id;
        });

        await Assert.That(await library.ReconcileImplicitAlbumMonitoringAsync()).IsEqualTo(0);
        await Assert.That((await library.GetAlbumAsync(albumId))!.Monitored).IsTrue();
    }

    static int SeedSongOnlyAlbum(TestMusicDb db)
    {
        int albumId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Ella Langley", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Dandelion", DeezerId = 123, TotalTrackCount = 3, Monitored = true, Added = DateTime.UtcNow };
            album.Tracks.Add(new MusicTrack { Title = "One", TrackNumber = 1, AbsoluteTrackNumber = 1, DeezerId = 111, HasFile = true, Monitored = true });
            album.Tracks.Add(new MusicTrack { Title = "Two", TrackNumber = 2, AbsoluteTrackNumber = 2, DeezerId = 222, Monitored = true });
            album.Tracks.Add(new MusicTrack { Title = "Three", TrackNumber = 3, AbsoluteTrackNumber = 3, DeezerId = 333, Monitored = true });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            albumId = album.Id;

            // The song grab that brought the album into the library.
            d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = album.Id,
                Source = "torrent",
                Identifier = "abc123",
                State = "Grabbed",
                DeezerTrackId = 111,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        });
        return albumId;
    }
}
