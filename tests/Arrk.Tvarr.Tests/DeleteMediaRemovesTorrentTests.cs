using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.TestSupport;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Services;
using Arrk.Tvarr.Tests.Fakes;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Deleting a movie/series must also delete its torrents from the download client. Radarr/Sonarr's
/// own delete endpoint leaves the download running, so Arrk resolves the torrent hashes from
/// the queue (in-progress) and history (finished) and removes them itself.
/// </summary>
public class DeleteMediaRemovesTorrentTests
{
    static TorrentItem Torrent(string hash) => new() { Hash = hash, Name = hash };

    [Test]
    public async Task DeleteMovie_DeletesQueuedAndHistoricalTorrents()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string json = (request.Method, path) switch
                {
                    (_, "/api/v3/movie/10") => """{ "id": 10, "title": "The Matrix", "year": 1999, "tmdbId": 603, "imdbId": "tt0133093" }""",
                    (_, "/api/v3/queue") => """{ "records": [ { "id": 1, "movieId": 10, "downloadId": "AAAABBBB" } ] }""",
                    (_, "/api/v3/history") => """{ "records": [ { "id": 1, "eventType": "grabbed", "downloadId": "AAAABBBB" }, { "id": 2, "eventType": "downloadFolderImported", "downloadId": "CCCCDDDD" } ] }""",
                    _ => "{}"
                };
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
            }
        };

        RecordingTorrentDownloader torrents = new();
        torrents.Torrents.AddRange([Torrent("aaaabbbb"), Torrent("ccccdddd"), Torrent("unrelated")]);

        RadarrService service = new(
            new RadarrClient("http://localhost", "key", handler),
            NullLogger<RadarrService>.Instance,
            torrents: torrents);

        await service.DeleteMovie(10, deleteFiles: true);

        await Assert.That(torrents.Deleted.Select(d => d.Id)).Contains("aaaabbbb");
        await Assert.That(torrents.Deleted.Select(d => d.Id)).Contains("ccccdddd");
        await Assert.That(torrents.Deleted.Select(d => d.Id)).DoesNotContain("unrelated");
        await Assert.That(torrents.Deleted.All(d => d.Cleanup == TorrentFileCleanup.DeleteFiles)).IsTrue();
        await Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath == "/api/v3/movie/10")).IsTrue();
    }

    [Test]
    public async Task DeleteSeries_DeletesQueuedAndHistoricalTorrents()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string json = (request.Method, path) switch
                {
                    (_, "/api/v3/series/7") => """{ "id": 7, "title": "Polar Opposites", "year": 2025, "tvdbId": 457078 }""",
                    (_, "/api/v3/queue") => """{ "records": [ { "id": 1, "seriesId": 7, "downloadId": "EEEEFFFF" } ] }""",
                    (_, "/api/v3/history") => """{ "records": [ { "id": 1, "eventType": "grabbed", "downloadId": "GGGGHHHH" } ] }""",
                    _ => "{}"
                };
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
            }
        };

        RecordingTorrentDownloader torrents = new();
        torrents.Torrents.AddRange([Torrent("eeeeffff"), Torrent("gggghhhh"), Torrent("other")]);

        SonarrService service = new(
            new SonarrClient("http://localhost", "key", handler),
            NullLogger<SonarrService>.Instance,
            torrents: torrents);

        await service.DeleteSeries(7, deleteFiles: true);

        await Assert.That(torrents.Deleted.Select(d => d.Id)).Contains("eeeeffff");
        await Assert.That(torrents.Deleted.Select(d => d.Id)).Contains("gggghhhh");
        await Assert.That(torrents.Deleted.Select(d => d.Id)).DoesNotContain("other");
        await Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath == "/api/v3/series/7")).IsTrue();
    }

    [Test]
    public async Task DeleteMovie_KeepFilesWhenNotDeletingFiles()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string json = (request.Method, path) switch
                {
                    (_, "/api/v3/movie/10") => """{ "id": 10, "title": "The Matrix", "tmdbId": 603 }""",
                    (_, "/api/v3/queue") => """{ "records": [ { "id": 1, "movieId": 10, "downloadId": "AAAABBBB" } ] }""",
                    (_, "/api/v3/history") => """{ "records": [] }""",
                    _ => "{}"
                };
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
            }
        };

        RecordingTorrentDownloader torrents = new();
        torrents.Torrents.Add(Torrent("aaaabbbb"));

        RadarrService service = new(
            new RadarrClient("http://localhost", "key", handler),
            NullLogger<RadarrService>.Instance,
            torrents: torrents);

        await service.DeleteMovie(10, deleteFiles: false);

        await Assert.That(torrents.Deleted.Single().Cleanup).IsEqualTo(TorrentFileCleanup.KeepFiles);
    }

    [Test]
    public async Task DeleteMovie_WithoutTorrentDownloader_StillDeletesFromArr()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string json = (request.Method, path) switch
                {
                    (_, "/api/v3/movie/10") => """{ "id": 10, "title": "The Matrix", "tmdbId": 603 }""",
                    _ => "{}"
                };
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
            }
        };

        RadarrService service = new(
            new RadarrClient("http://localhost", "key", handler),
            NullLogger<RadarrService>.Instance);

        await service.DeleteMovie(10, deleteFiles: true);

        await Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath == "/api/v3/movie/10")).IsTrue();
    }
}
