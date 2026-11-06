using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.Music;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

public class MusicCatalogServiceTests
{
    static (MusicCatalogService Catalog, FakeHttpMessageHandler Deezer, FakeHttpMessageHandler Mb, TestMusicDb Db) Build()
    {
        FakeHttpMessageHandler deezerHandler = new();
        FakeHttpMessageHandler mbHandler = new();

        TestMusicDb db = new();
        // Seed a small library so membership annotation has something to match.
        db.Seed(d => d.Set<MusicArtist>().Add(new MusicArtist { Name = "Radiohead", Added = DateTime.UtcNow }));
        MusicLibrary library = MusicTestHelpers.Library(db);

        DeezerService deezer = new(null, deezerHandler);
        MusicAcquisitionService acquisition = MusicTestHelpers.Acquisition(
            library, db, MusicTestHelpers.EmptyHandler(), MusicTestHelpers.EmptyHandler(), deezerHandler: deezerHandler);
        MusicCatalogService catalog = new(deezer, library, acquisition, NullLogger<MusicCatalogService>.Instance);
        return (catalog, deezerHandler, mbHandler, db);
    }

    [Test]
    public async Task Search_SectionsByType_AndLiftsMatchingAlbumToTop()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, _) = Build();
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("search/artist")
                ? """{ "data": [ { "id": 99, "name": "Halo", "picture_xl": "http://cdn/artist.jpg" } ] }"""
                : path.Contains("search/album")
                    ? """
                      { "data": [
                          { "type": "album", "id": 10, "title": "Halo 3 (Original Soundtrack)",
                            "artist": { "id": 2, "name": "Martin O'Donnell" }, "cover_xl": "http://cdn/cover.jpg" },
                          { "id": 11, "title": "Halo 3", "artist": { "id": 3, "name": "Eric Spinelli" } } ] }
                      """
                    : """
                      { "data": [ { "id": 7, "title": "halo 3", "artist": { "id": 5, "name": "Brandon Nembhard" },
                          "preview": "http://p/preview.mp3", "album": { "id": 1, "cover_xl": "http://cdn/x.jpg" } } ] }
                      """;
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        MusicSearchResult results = await catalog.SearchAsync("halo 3");

        await Assert.That(results.Albums.Count).IsEqualTo(2);
        await Assert.That(results.Artists.Count).IsEqualTo(1);
        await Assert.That(results.Tracks.Count).IsEqualTo(1);
        // The soundtrack album (not the junk track literally named "halo 3") is the top result.
        await Assert.That(results.TopResult).IsNotNull();
        await Assert.That(results.TopResult!.EntityType).IsEqualTo(MusicEntityType.Album);
        await Assert.That(results.TopResult!.Title).IsEqualTo("Halo 3 (Original Soundtrack)");
        await Assert.That(results.TopResult!.ArtUrl).IsEqualTo("http://cdn/cover.jpg");
    }

    [Test]
    public async Task Search_ShortQuery_ReturnsEmpty()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, _) = Build();

        MusicSearchResult results = await catalog.SearchAsync("x");

        await Assert.That(results.IsEmpty).IsTrue();
        await Assert.That(deezer.Requests).IsEmpty(); // short queries never hit the network
    }

    [Test]
    public async Task Search_IncludesEditorialPlaylists()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, _) = Build();
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("search/playlist")
                ? """{ "data": [ { "id": 1111142221, "title": "Top UK", "nb_tracks": 100, "picture_xl": "http://cdn/pl.jpg", "user": { "name": "Deezer Charts" } } ] }"""
                : """{ "data": [] }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        MusicSearchResult results = await catalog.SearchAsync("top uk");

        await Assert.That(results.Playlists.Count).IsEqualTo(1);
        MusicItem playlist = results.Playlists.Single();
        await Assert.That(playlist.EntityType).IsEqualTo(MusicEntityType.Playlist);
        await Assert.That(playlist.DeezerId).IsEqualTo(1111142221L);
        await Assert.That(playlist.TrackCount).IsEqualTo(100);
        await Assert.That(results.Total).IsEqualTo(1);
        await Assert.That(deezer.Requests.Any(r => r.Uri.AbsolutePath.Contains("search/playlist"))).IsTrue();
    }

    [Test]
    public async Task GetChartPlaylists_RequestsTheFullFeaturedSet()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, _) = Build();
        deezer.EnqueueJson("""{ "data": [] }""");

        await catalog.GetChartPlaylistsAsync();

        CapturedRequest request = deezer.Requests.Single();
        await Assert.That(request.Uri.AbsolutePath).IsEqualTo("/chart/0/playlists");
        await Assert.That(request.Uri.Query).Contains("limit=100");
    }

    [Test]
    public async Task GetGenrePlaylists_UsesGenreChart_AndMapsEditorialPlaylists()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, _) = Build();
        deezer.EnqueueJson("""
        { "data": [ { "id": 867825522, "title": "80s Hits", "nb_tracks": 100, "picture_xl": "http://cdn/80s.jpg", "user": { "name": "Deezer Pop Editor" } } ] }
        """);

        List<MusicItem> playlists = await catalog.GetGenrePlaylistsAsync(132, 100);

        MusicItem playlist = playlists.Single();
        await Assert.That(playlist.Title).IsEqualTo("80s Hits");
        await Assert.That(playlist.EntityType).IsEqualTo(MusicEntityType.Playlist);
        await Assert.That(playlist.TrackCount).IsEqualTo(100);
        await Assert.That(playlist.ArtUrl).IsEqualTo("http://cdn/80s.jpg");
        CapturedRequest request = deezer.Requests.Single();
        await Assert.That(request.Uri.AbsolutePath).IsEqualTo("/chart/132/playlists");
        await Assert.That(request.Uri.Query).Contains("limit=100");
    }

    [Test]
    public async Task CountryChart_ResolvesOfficialDeezerChartsPlaylist()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, _) = Build();
        deezer.Fallback = (req, _) =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            string json = url.Contains("search/playlist")
                ? """{ "data": [ { "id": 1111142221, "title": "Top UK", "nb_tracks": 100, "picture_xl": "http://cdn/pl.jpg", "user": { "name": "Deezer Charts" } } ] }"""
                : """
                  { "data": [ { "id": 7, "title": "Song A", "artist": { "id": 1, "name": "Artist A" },
                      "preview": "http://p/a.mp3", "album": { "id": 2, "cover_xl": "http://cdn/a.jpg" } } ] }
                  """;
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        List<MusicItem> tracks = await catalog.GetCountryChartTracksAsync("gb");

        await Assert.That(tracks.Count).IsEqualTo(1);
        await Assert.That(tracks[0].Title).IsEqualTo("Song A");
        await Assert.That(tracks[0].EntityType).IsEqualTo(MusicEntityType.Track);
        await Assert.That(tracks[0].PreviewUrl).IsEqualTo("http://p/a.mp3");
    }

    [Test]
    public async Task CountryChart_CountryWithoutNationalChart_FallsBackToGlobal()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, _) = Build();
        deezer.Fallback = (req, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            """{ "data": [ { "id": 9, "title": "Global Hit", "artist": { "id": 1, "name": "Someone" }, "album": { "id": 3 } } ] }"""));

        List<MusicItem> tracks = await catalog.GetCountryChartTracksAsync("in");

        await Assert.That(tracks.Count).IsEqualTo(1);
        await Assert.That(tracks[0].Title).IsEqualTo("Global Hit");
        // India has no national chart, so only the global chart endpoint is called.
        await Assert.That(deezer.Requests.Any(r => r.Uri.AbsolutePath.Contains("chart/"))).IsTrue();
        await Assert.That(deezer.Requests.Any(r => r.Uri.AbsolutePath.Contains("search/playlist"))).IsFalse();
    }

    [Test]
    public async Task ChartTracks_ReturnsDeezerSongs()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, _) = Build();
        deezer.EnqueueJson("""
        { "data": [ { "id": 7, "title": "Creep", "artist": { "id": 399, "name": "Radiohead" }, "preview": "http://p/preview.mp3",
            "album": { "id": 1, "cover_xl": "http://cdn/c.jpg" } } ] }
        """);

        List<MusicItem> tracks = await catalog.GetChartTracksAsync();

        MusicItem t = tracks.Single();
        await Assert.That(t.EntityType).IsEqualTo(MusicEntityType.Track);
        await Assert.That(t.PreviewUrl).IsEqualTo("http://p/preview.mp3");
        await Assert.That(t.ArtistDeezerId).IsEqualTo(399);
    }

    [Test]
    public async Task ChartArtists_AnnotatesLibraryMembershipByName()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, _) = Build();
        deezer.EnqueueJson("""
        { "data": [ { "id": 399, "name": "Radiohead", "picture_xl": "http://cdn/r.jpg" },
                    { "id": 664, "name": "Jeff Buckley" } ] }
        """);

        List<MusicItem> artists = await catalog.GetChartArtistsAsync();

        await Assert.That(artists.First(a => a.Title == "Radiohead").IsInLibrary).IsTrue();
        await Assert.That(artists.First(a => a.Title == "Jeff Buckley").IsInLibrary).IsFalse();
    }

    [Test]
    public async Task ChartAlbums_MarksHeldAlbumWithFileState_SoCardsShowInLibrary()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, TestMusicDb db) = Build();
        int libraryId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Michael Jackson", DeezerId = 259, Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Thriller", DeezerId = 10, TotalTrackCount = 1, Added = DateTime.UtcNow };
            album.Tracks.Add(new MusicTrack { Title = "Wanna Be Startin' Somethin'", TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            libraryId = album.Id;
        });
        deezer.EnqueueJson("""
        { "data": [ { "id": 10, "title": "Thriller", "artist": { "id": 259, "name": "Michael Jackson" },
            "cover_xl": "http://cdn/thriller.jpg", "nb_tracks": 1 } ] }
        """);

        List<MusicItem> albums = await catalog.GetChartAlbumsAsync();

        MusicItem held = albums.Single(a => a.DeezerId == 10);
        await Assert.That(held.IsInLibrary).IsTrue();
        await Assert.That(held.LibraryId).IsEqualTo(libraryId);
        await Assert.That(held.HasFile).IsTrue();
        await Assert.That(held.LibraryStatus).IsEqualTo("Downloaded");
    }

    [Test]
    public async Task ChartArtists_AnnotatesLibraryMembershipByDeezerId()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, TestMusicDb db) = Build();
        db.Seed(d => d.Set<MusicArtist>().Add(new MusicArtist { Name = "Radiohead", DeezerId = 399, Added = DateTime.UtcNow }));
        deezer.EnqueueJson("""
        { "data": [ { "id": 399, "name": "Radiohead (remastered)" },
                    { "id": 664, "name": "Jeff Buckley" } ] }
        """);

        List<MusicItem> artists = await catalog.GetChartArtistsAsync();

        // Matched by Deezer id even though the display name differs.
        await Assert.That(artists.First(a => a.DeezerId == 399).IsInLibrary).IsTrue();
        await Assert.That(artists.First(a => a.DeezerId == 664).IsInLibrary).IsFalse();
    }

    [Test]
    public async Task ChartTracks_MarksSongInLibrary_WhenItsAlbumIsHeld()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, TestMusicDb db) = Build();
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Radiohead", DeezerId = 399, Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "In Rainbows", DeezerId = 10, TotalTrackCount = 1, Added = DateTime.UtcNow };
            album.Tracks.Add(new MusicTrack { Title = "15 Step", TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
        });
        deezer.EnqueueJson("""
        { "data": [ { "id": 7, "title": "15 Step", "artist": { "id": 399, "name": "Radiohead" },
            "album": { "id": 10, "title": "In Rainbows", "cover_xl": "http://cdn/c.jpg" } } ] }
        """);

        List<MusicItem> tracks = await catalog.GetChartTracksAsync();

        MusicItem track = tracks.Single();
        await Assert.That(track.IsInLibrary).IsTrue();
        await Assert.That(track.HasFile).IsTrue();
    }

    [Test]
    public async Task QualityProfiles_ReturnTheMusicDefaults()
    {
        (MusicCatalogService catalog, _, _, _) = Build();

        List<(int Id, string Name)> profiles = await catalog.GetQualityProfilesAsync();

        await Assert.That(profiles.Count).IsEqualTo(3);
        await Assert.That(profiles[0].Name).IsEqualTo("Best Available");
        await Assert.That(profiles[1].Name).IsEqualTo("Lossless");
        await Assert.That(profiles[2].Name).IsEqualTo("Lossy");
    }

    [Test]
    public async Task GetAlbum_MarksInLibrary_WhenWeAlreadyHoldIt()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, TestMusicDb db) = Build();
        int libraryId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Michael Jackson", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Thriller", DeezerId = 10 };
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            libraryId = album.Id;
        });
        deezer.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            """{ "id": 10, "title": "Thriller", "artist": { "id": 1, "name": "Michael Jackson" }, "nb_tracks": 9, "tracks": { "data": [] } }"""));

        MusicItem? album = await catalog.GetAlbumAsync(10);

        await Assert.That(album).IsNotNull();
        await Assert.That(album!.IsInLibrary).IsTrue();
        await Assert.That(album.LibraryId).IsEqualTo(libraryId);
    }

    [Test]
    public async Task Countries_OnlyListsNationsThatHaveADeezerChart()
    {
        await Assert.That(MusicCatalogService.Countries.First(c => c.Code == "gb").DeezerName).IsEqualTo("UK");
        await Assert.That(MusicCatalogService.Countries.First(c => c.Code == "cz").DeezerName).IsEqualTo("Czech Republic");
        // India has no official Deezer chart, so it must not appear and fall back to the global chart
        // under an Indian label.
        await Assert.That(MusicCatalogService.Countries.Any(c => c.Code == "in")).IsFalse();
    }

    [Test]
    public async Task ArtUrls_PointAtCachedEndpoint()
    {
        await Assert.That(MusicCatalogService.ArtistArtUrl("Radiohead")).IsEqualTo("/api/music/art/artist?name=Radiohead");
        await Assert.That(MusicCatalogService.AlbumArtUrl("Radiohead", "In Rainbows"))
            .IsEqualTo("/api/music/art/album?artist=Radiohead&title=In%20Rainbows");
    }

    [Test]
    public async Task TrackArtUrl_AcceptsCoverIdsAndAlreadyResolvedUrls()
    {
        await Assert.That(MusicCatalogService.TrackArtUrl("Radiohead", "In Rainbows", "mf-123"))
            .IsEqualTo("/api/music/player/cover/mf-123");

        // Play history persists the queue's cover URL, so resolving it again must not double-proxy.
        await Assert.That(MusicCatalogService.TrackArtUrl("Radiohead", "In Rainbows", "/api/music/player/cover/mf-123"))
            .IsEqualTo("/api/music/player/cover/mf-123");
        await Assert.That(MusicCatalogService.TrackArtUrl("Radiohead", "In Rainbows", "https://cdn.example/art.jpg"))
            .IsEqualTo("https://cdn.example/art.jpg");

        await Assert.That(MusicCatalogService.TrackArtUrl("Radiohead", "In Rainbows", ""))
            .IsEqualTo("/api/music/art/album?artist=Radiohead&title=In%20Rainbows");
    }

    [Test]
    public async Task GetRecentlyAdded_ReturnsHeldAlbums_NewestFirst()
    {
        (MusicCatalogService catalog, _, _, TestMusicDb db) = Build();
        int olderId = 0;
        int newerId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Radiohead", Added = DateTime.UtcNow };
            MusicAlbum older = new() { Title = "Pablo Honey", Added = DateTime.UtcNow.AddDays(-2) };
            older.Tracks.Add(new MusicTrack { Title = "Creep", TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true });
            MusicAlbum newer = new() { Title = "In Rainbows", Added = DateTime.UtcNow };
            newer.Tracks.Add(new MusicTrack { Title = "15 Step", TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true });
            artist.Albums.Add(older);
            artist.Albums.Add(newer);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            olderId = older.Id;
            newerId = newer.Id;
        });

        List<MusicItem> recent = await catalog.GetRecentlyAddedAsync();

        await Assert.That(recent.Count).IsEqualTo(2);
        await Assert.That(recent[0].Title).IsEqualTo("In Rainbows");
        await Assert.That(recent[0].LibraryId).IsEqualTo(newerId);
        await Assert.That(recent[1].LibraryId).IsEqualTo(olderId);
        await Assert.That(recent.All(a => a.HasFile)).IsTrue();
    }

    [Test]
    public async Task BackfillTrackDeezerIds_FillsMissingIdsFromDeezerDetail()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, TestMusicDb db) = Build();
        int trackId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Goo Goo Dolls", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Dizzy Up the Girl", DeezerId = 55, Added = DateTime.UtcNow };
            MusicTrack track = new() { Title = "Iris", DiscNumber = 1, TrackNumber = 1, AbsoluteTrackNumber = 1 };
            album.Tracks.Add(track);
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            trackId = track.Id;
        });
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/album/55/tracks")
                ? """{ "data": [ { "id": 777, "title": "Iris", "track_position": 1, "disk_number": 1 } ] }"""
                : """{ "id": 55, "title": "Dizzy Up the Girl", "artist": { "id": 9, "name": "Goo Goo Dolls" }, "nb_tracks": 1 }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        int updated = await catalog.BackfillTrackDeezerIdsAsync();

        await Assert.That(updated).IsEqualTo(1);
        using ArrkDbContext ctx = db.CreateDbContext();
        await Assert.That(ctx.Set<MusicTrack>().Single(t => t.Id == trackId).DeezerId).IsEqualTo(777);
    }

    [Test]
    public async Task GetLibraryAlbum_MergesDeezerPreviews()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, TestMusicDb db) = Build();
        int albumId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Electric Light Orchestra", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Out of the Blue", DeezerId = 77, Added = DateTime.UtcNow };
            album.Tracks.Add(new MusicTrack { Title = "Mr. Blue Sky", DiscNumber = 1, TrackNumber = 1, AbsoluteTrackNumber = 1 });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            albumId = album.Id;
        });
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/album/77/tracks")
                ? """{ "data": [ { "id": 1, "title": "Mr. Blue Sky", "track_position": 1, "disk_number": 1, "preview": "http://cdn/preview.mp3" } ] }"""
                : """{ "id": 77, "title": "Out of the Blue", "artist": { "id": 9, "name": "Electric Light Orchestra" }, "nb_tracks": 1 }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        MusicItem? album = await catalog.GetLibraryAlbumAsync(albumId);

        await Assert.That(album).IsNotNull();
        await Assert.That(album!.Tracks[0].PreviewUrl).IsEqualTo("http://cdn/preview.mp3");
    }

    [Test]
    public async Task RescueUnidentified_RebindsJunkAlbumToDeezer()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, TestMusicDb db) = Build();
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
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("search/album")
                ? """{ "data": [] }"""
                : path.Contains("search/track")
                    ? """{ "data": [ { "id": 1, "title": "Mr. Blue Sky", "artist": { "id": 9, "name": "Electric Light Orchestra" }, "album": { "id": 77, "title": "Out of the Blue" } } ] }"""
                    : path.Contains("/album/77/tracks")
                        ? """{ "data": [ { "id": 100, "title": "Mr. Blue Sky", "track_position": 1, "disk_number": 1 } ] }"""
                        : """{ "id": 77, "title": "Out of the Blue", "artist": { "id": 9, "name": "Electric Light Orchestra" }, "nb_tracks": 1 }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        MusicRescueResult result = await catalog.RescueUnidentifiedAsync();

        await Assert.That(result.Rescued).IsEqualTo(1);
        using ArrkDbContext ctx = db.CreateDbContext();
        MusicAlbum upgraded = ctx.Set<MusicAlbum>().Single(a => a.Id == albumId);
        await Assert.That(upgraded.DeezerId).IsEqualTo(77);
        await Assert.That(upgraded.Title).IsEqualTo("Out of the Blue");
        await Assert.That(ctx.Set<MusicTrack>().Any(t => t.AlbumId == albumId && t.HasFile)).IsTrue();
    }

    [Test]
    public async Task LibrarySongs_ListDownloadedAndWantedOnly_NotUnrequestedTracks()
    {
        (MusicCatalogService catalog, _, _, TestMusicDb db) = Build();
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Ella Langley", Added = DateTime.UtcNow };
            // A song-only album: one held song, two tracks we never asked for.
            MusicAlbum partial = new() { Title = "Dandelion", DeezerId = 1, TotalTrackCount = 3, Monitored = false, Added = DateTime.UtcNow };
            partial.Tracks.Add(new MusicTrack { Title = "One", TrackNumber = 1, AbsoluteTrackNumber = 1, DeezerId = 11, HasFile = true, Monitored = false });
            partial.Tracks.Add(new MusicTrack { Title = "Two", TrackNumber = 2, AbsoluteTrackNumber = 2, DeezerId = 22, Monitored = false });
            // An explicitly wanted album: its missing song is genuinely Missing.
            MusicAlbum wanted = new() { Title = "Requested", DeezerId = 2, TotalTrackCount = 1, Monitored = true, Added = DateTime.UtcNow };
            wanted.Tracks.Add(new MusicTrack { Title = "Wanted", TrackNumber = 1, AbsoluteTrackNumber = 1, DeezerId = 33, Monitored = true });
            artist.Albums.Add(partial);
            artist.Albums.Add(wanted);
            d.Set<MusicArtist>().Add(artist);
        });

        List<MusicItem> songs = await catalog.GetLibrarySongsAsync();

        await Assert.That(songs.Any(s => s.Title == "One")).IsTrue();
        await Assert.That(songs.Any(s => s.Title == "Wanted")).IsTrue();
        await Assert.That(songs.Any(s => s.Title == "Two")).IsFalse();
        await Assert.That(songs.Single(s => s.Title == "One").LibraryStatus).IsEqualTo("Downloaded");
        await Assert.That(songs.Single(s => s.Title == "Wanted").LibraryStatus).IsEqualTo("Missing");
    }

    [Test]
    public async Task RecentlyAdded_ReportsASongOnlyAlbumAsPartial_NotMissing()
    {
        (MusicCatalogService catalog, _, _, TestMusicDb db) = Build();
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Ella Langley", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Dandelion", DeezerId = 1, TotalTrackCount = 3, Monitored = false, Added = DateTime.UtcNow };
            album.Tracks.Add(new MusicTrack { Title = "One", TrackNumber = 1, AbsoluteTrackNumber = 1, DeezerId = 11, HasFile = true });
            album.Tracks.Add(new MusicTrack { Title = "Two", TrackNumber = 2, AbsoluteTrackNumber = 2, DeezerId = 22 });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
        });

        MusicItem albumItem = (await catalog.GetRecentlyAddedAsync()).Single(a => a.Title == "Dandelion");

        await Assert.That(albumItem.LibraryStatus).IsEqualTo("Partial");
        await Assert.That(albumItem.IsMonitored).IsFalse();
    }

    [Test]
    public async Task ChartAlbums_MarkAnUnmonitoredHeldAlbumUnmonitored()
    {
        (MusicCatalogService catalog, FakeHttpMessageHandler deezer, _, TestMusicDb db) = Build();
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Michael Jackson", DeezerId = 259, Added = DateTime.UtcNow };
            artist.Albums.Add(new MusicAlbum { Title = "Thriller", DeezerId = 10, Monitored = false, Added = DateTime.UtcNow });
            d.Set<MusicArtist>().Add(artist);
        });
        deezer.EnqueueJson("""
        { "data": [ { "id": 10, "title": "Thriller", "artist": { "id": 259, "name": "Michael Jackson" }, "nb_tracks": 9 } ] }
        """);

        MusicItem held = (await catalog.GetChartAlbumsAsync()).Single(a => a.DeezerId == 10);

        await Assert.That(held.IsInLibrary).IsTrue();
        await Assert.That(held.LibraryStatus).IsEqualTo("Unmonitored");
    }
}
