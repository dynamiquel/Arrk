using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Models;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// Covers import identity: a single requested song binds to the exact Deezer track it was asked for,
/// a title tie is broken by the file's own length, and an album is never persisted without a track
/// list, because a rows-less album makes every song that resolves to it loop.
/// </summary>
public class MusicImportIdentityTests
{
    // Album identity

    [Test]
    public async Task AddOrUpdateAlbum_WithNoTracks_IsNotPersisted()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        // A Deezer album detail whose track fetch failed (or a summary) carries no track list.
        MusicItem album = new()
        {
            EntityType = MusicEntityType.Album,
            Title = "Jazz",
            ArtistName = "Queen",
            DeezerId = 1121401,
            Tracks = []
        };

        Album? added = await library.AddOrUpdateAlbumAsync(album);

        await Assert.That(added).IsNull();
        await Assert.That(await library.GetAllAlbumsAsync()).IsEmpty();
    }

    [Test]
    public async Task AddOrUpdateAlbum_WithNoTracks_KeepsAnExistingAlbumsTrackList()
    {
        using TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        MusicItem withTracks = new()
        {
            EntityType = MusicEntityType.Album,
            Title = "Jazz",
            ArtistName = "Queen",
            DeezerId = 1121401,
            Tracks = [new MusicTrackItem { Number = 1, DiscNumber = 1, Title = "Mustapha", DurationSeconds = 182, DeezerId = 111 }]
        };
        Album? created = await library.AddOrUpdateAlbumAsync(withTracks);
        await Assert.That(created).IsNotNull();
        await Assert.That(created!.Tracks).HasCount(1);

        // A later refresh that returns no track list must not wipe the tracks we already hold.
        Album? updated = await library.AddOrUpdateAlbumAsync(withTracks with { Tracks = [] });

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tracks).HasCount(1);
        await Assert.That(updated.Tracks[0].Title).IsEqualTo("Mustapha");
    }

    // Deezer track-list fallback

    [Test]
    public async Task Deezer_GetAlbum_FallsBackToTheEmbeddedTracklist_WhenTheTracksEndpointFails()
    {
        // Some albums' /tracks sub-resource answers "no data" (Deezer error 800) while the album
        // detail embeds the whole tracklist.
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.EndsWith("/tracks")
                ? """{ "error": { "type": "DataException", "message": "no data", "code": 800 } }"""
                : """
                  { "id": 1121401, "title": "Jazz (2011 Remaster)", "artist": { "id": 412, "name": "Queen" },
                    "nb_tracks": 1, "record_type": "album",
                    "tracks": { "data": [ { "id": 12209331, "title": "Don't Stop Me Now (Remastered 2011)",
                        "duration": 212, "track_position": 12, "disk_number": 1 } ] } }
                  """;
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        DeezerService deezer = new(NullLogger<DeezerService>.Instance, handler);

        MusicItem? album = await deezer.GetAlbumAsync(1121401);

        await Assert.That(album).IsNotNull();
        await Assert.That(album!.Tracks).HasCount(1);
        await Assert.That(album.Tracks[0].DeezerId).IsEqualTo(12209331);
        await Assert.That(album.Tracks[0].DurationSeconds).IsEqualTo(212);
    }

    // Title-tie disambiguation

    static List<Track> SiblingTracks() =>
    [
        new() { Id = 1, Title = "Can You Feel the Love Tonight", DurationSeconds = 178, TrackNumber = 5, AbsoluteTrackNumber = 5, MediumNumber = 1 },
        new() { Id = 2, Title = "Can You Feel the Love Tonight (End Title)", DurationSeconds = 243, TrackNumber = 12, AbsoluteTrackNumber = 12, MediumNumber = 1 }
    ];

    [Test]
    public async Task MatchTrackByTitle_PrefersTheClosestDuration_WhenSiblingTitlesCollide()
    {
        // A ~241s file is the End Title, not the 178s album version, the length picks the sibling.
        Track? match = MusicAcquisitionService.MatchTrackByTitle(
            SiblingTracks(), [], "Can You Feel the Love Tonight", preferDurationSeconds: 241);

        await Assert.That(match).IsNotNull();
        await Assert.That(match!.Id).IsEqualTo(2);
    }

    [Test]
    public async Task MatchTrackByTitle_KeepsTheFirstMatch_WhenNoLengthIsKnown()
    {
        // Without a probed length the behaviour is unchanged: the first title match wins.
        Track? match = MusicAcquisitionService.MatchTrackByTitle(
            SiblingTracks(), [], "Can You Feel the Love Tonight");

        await Assert.That(match).IsNotNull();
        await Assert.That(match!.Id).IsEqualTo(1);
    }
}
