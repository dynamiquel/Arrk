using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Ai;
using Arrk.Ai.Data;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.TestSupport;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Exercises the real import pipeline against scripted Radarr/Sonarr transports: stage a file,
/// confirm the arr's manual-import command is sent for the right entity, and verify the staged copy
/// and database row are closed out after success.
/// </summary>
public class TvImportServiceTests
{
    sealed record Dependencies(
        TvUploadStore Store,
        string Root,
        TestDbContextFactory Db,
        RadarrClient RadarrClient,
        SonarrClient SonarrClient,
        RadarrService Radarr,
        SonarrService Sonarr,
        EventBus Bus);

    static Dependencies BuildDependencies(
        FakeHttpMessageHandler radarrHandler,
        FakeHttpMessageHandler sonarrHandler)
    {
        string root = Path.Combine(Path.GetTempPath(), $"stackarr-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        ArrkConfig config = new()
        {
            Storage = new StorageConfig
            {
                Downloads = Path.Combine(root, "downloads"),
                Media = Path.Combine(root, "media")
            }
        };

        TvConfig tv = new()
        {
            Uploads = new TvUploadConfig { MaxFileSizeGb = 1, DeleteAfterImport = true }
        };

        TestDbContextFactory db = new(TestDb.Options());
        TvUploadStore store = new(
            db,
            new MutableOptionsMonitor<ArrkConfig>(config),
            new MutableOptionsMonitor<TvConfig>(tv),
            NullLogger<TvUploadStore>.Instance);

        RadarrClient radarrClient = new("http://localhost", "key", radarrHandler);
        SonarrClient sonarrClient = new("http://localhost", "key", sonarrHandler);
        RadarrService radarr = new(radarrClient, NullLogger<RadarrService>.Instance);
        SonarrService sonarr = new(sonarrClient, NullLogger<SonarrService>.Instance);

        return new(store, root, db, radarrClient, sonarrClient, radarr, sonarr, new EventBus());
    }

    static TvImportService BuildService(Dependencies deps, string root)
    {
        TmdbService tmdb = new("", "", NullLogger<TmdbService>.Instance, new FakeHttpMessageHandler());
        MediaCatalogService catalog = new(tmdb, deps.Radarr, deps.Sonarr, NullLogger<MediaCatalogService>.Instance);

        MutableOptionsMonitor<AiConfig> aiConfig = new(new AiConfig { Enabled = false });
        AiUsageService usage = new(deps.Db, TimeProvider.System);
        AiService ai = new(
            new AiClientFactory(NullLogger<AiClientFactory>.Instance, new FakeHttpMessageHandler()),
            aiConfig,
            NullLogger<AiService>.Instance,
            usage,
            new AiBudget(usage, TimeProvider.System));
        TvImportAiMatcher aiMatcher = new(
            ai,
            new AiDecisionService(deps.Db, TimeProvider.System),
            aiConfig,
            NullLogger<TvImportAiMatcher>.Instance);

        ArrMediaPathMapper mapper = new(
        [
            ("/movies", Path.Combine(root, "media", "tv", "movies")),
            ("/shows", Path.Combine(root, "media", "tv", "shows"))
        ]);

        return new(
            deps.Store,
            deps.RadarrClient,
            deps.SonarrClient,
            deps.Radarr,
            deps.Sonarr,
            catalog,
            aiMatcher,
            mapper,
            deps.Bus,
            NullLogger<TvImportService>.Instance);
    }

    static MemoryStream Content() => new(Encoding.UTF8.GetBytes("fake video bytes"));

    static string MovieBefore => """
        {
          "id": 5,
          "title": "Moana",
          "year": 2026,
          "tmdbId": 123,
          "path": "/movies/123",
          "hasFile": false,
          "qualityProfileId": 1
        }
        """;

    static string MovieAfter(string path) => $$"""
        {
          "id": 5,
          "title": "Moana",
          "year": 2026,
          "tmdbId": 123,
          "path": "/movies/123",
          "hasFile": true,
          "qualityProfileId": 1,
          "movieFile": {
            "id": 9,
            "path": "{{path}}",
            "size": 16,
            "quality": {
              "quality": { "id": 18, "name": "WEBDL-2160p", "source": "webdl", "resolution": 2160 },
              "revision": { "version": 1, "real": 0, "isRepack": false }
            }
          }
        }
        """;

    static string MovieSuggestion(string path) => $$"""
        [
          {
            "path": "{{path}}",
            "relativePath": "Moana.2026.1080p.mkv",
            "name": "Moana",
            "size": 16,
            "quality": {
              "quality": { "id": 18, "name": "WEBDL-2160p", "source": "webdl", "resolution": 2160 },
              "revision": { "version": 1, "real": 0, "isRepack": false }
            },
            "languages": [ { "id": 1, "name": "English" } ],
            "indexerFlags": 0,
            "rejections": []
          }
        ]
        """;

    const string CommandQueued = """{ "id": 77, "name": "ManualImport", "status": "queued" }""";
    const string CommandDone = """{ "id": 77, "name": "ManualImport", "status": "completed", "result": "successful", "message": "Manually imported 1 files" }""";

    [Test]
    public async Task MovieImport_SendsManualImportForTheMovieAndCleansUp()
    {
        string batch = Guid.NewGuid().ToString("N");
        FakeHttpMessageHandler radarrHandler = new();
        FakeHttpMessageHandler sonarrHandler = new();
        Dependencies deps = BuildDependencies(radarrHandler, sonarrHandler);
        TvUploadRecord staged = await deps.Store.StageAsync(
            Content(), "Moana.2026.1080p.mkv", 16, batch, "movie", "movie");

        string destination = Path.Combine(deps.Root, "media", "tv", "movies", "123", "Moana.mkv");
        int movieGets = 0;
        radarrHandler.Fallback = (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v3/movie/5")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(movieGets++ == 0 ? MovieBefore : MovieAfter(destination)));
            if (request.Method == HttpMethod.Get && path == "/api/v3/manualimport")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(MovieSuggestion(staged.ContainerPath)));
            if (request.Method == HttpMethod.Post && path == "/api/v3/manualimport")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(MovieSuggestion(staged.ContainerPath)));
            if (request.Method == HttpMethod.Post && path == "/api/v3/command")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(CommandQueued));
            if (request.Method == HttpMethod.Get && path == "/api/v3/command/77")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(CommandDone));
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse("[]"));
        };
        sonarrHandler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("[]"));

        TvImportService service = BuildService(deps, deps.Root);
        TvImportResult result = await service.ImportAsync(new TvImportRequest
        {
            BatchId = batch,
            MediaType = "movie",
            LibraryId = 5,
            TmdbId = 123,
            Title = "Moana",
            Year = 2026,
            QualityProfileId = 1
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Files.Single().Status).IsEqualTo(TvUploadStatus.Imported);
        await Assert.That(File.Exists(staged.StoredPath)).IsFalse();

        CapturedRequest command = radarrHandler.Requests.Single(request =>
            request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/v3/command");
        await Assert.That(command.Body).Contains("\"name\":\"ManualImport\"");
        await Assert.That(command.Body).Contains("\"importMode\":\"copy\"");
        await Assert.That(command.Body).Contains("\"movieId\":5");
        await Assert.That(command.Body).Contains("WEBDL-2160p");

        List<TvUploadRecord> rows = await deps.Store.GetBatchAsync(batch);
        await Assert.That(rows.Single().Status).IsEqualTo(TvUploadStatus.Imported);
        await Assert.That(rows.Single().LibraryId).IsEqualTo(5);
    }

    const string SeriesJson = """
        {
          "id": 10,
          "title": "Re: ZERO, Starting Life in Another World",
          "year": 2016,
          "tvdbId": 305089,
          "tmdbId": 65942,
          "path": "/shows/305089",
          "monitored": true,
          "seasons": [
            { "seasonNumber": 1, "monitored": true, "statistics": { "episodeCount": 2, "episodeFileCount": 0 } }
          ]
        }
        """;

    static string EpisodesJson(bool hasFile) => $$"""
        [
          {
            "id": 610,
            "seriesId": 10,
            "seasonNumber": 1,
            "episodeNumber": 1,
            "title": "The End of the Beginning",
            "monitored": true,
            "hasFile": {{(hasFile ? "true" : "false")}}
          }
        ]
        """;

    static string EpisodeSuggestion(string path) => $$"""
        [
          {
            "path": "{{path}}",
            "relativePath": "Re.Zero.S01E01.mkv",
            "name": "Re.Zero.S01E01",
            "size": 16,
            "quality": {
              "quality": { "id": 7, "name": "Bluray-1080p", "source": "bluray", "resolution": 1080 },
              "revision": { "version": 1, "real": 0, "isRepack": false }
            },
            "languages": [ { "id": 8, "name": "Japanese" } ],
            "series": {
              "id": 10,
              "title": "Re: ZERO, Starting Life in Another World",
              "year": 2016,
              "tvdbId": 305089,
              "tmdbId": 65942
            },
            "episodes": [
              {
                "id": 610,
                "seriesId": 10,
                "seasonNumber": 1,
                "episodeNumber": 1,
                "title": "The End of the Beginning",
                "hasFile": false
              }
            ],
            "indexerFlags": 0,
            "rejections": []
          }
        ]
        """;

    static string EpisodeFile(string path) => $$"""
        [
          {
            "id": 1,
            "seriesId": 10,
            "seasonNumber": 1,
            "path": "{{path}}",
            "size": 16,
            "episodeIds": [ 610 ],
            "quality": {
              "quality": { "id": 7, "name": "Bluray-1080p", "source": "bluray", "resolution": 1080 },
              "revision": { "version": 1, "real": 0, "isRepack": false }
            }
          }
        ]
        """;

    [Test]
    public async Task EpisodeImport_SendsManualImportForTheChosenEpisodeAndCleansUp()
    {
        string batch = Guid.NewGuid().ToString("N");
        FakeHttpMessageHandler radarrHandler = new();
        FakeHttpMessageHandler sonarrHandler = new();
        Dependencies deps = BuildDependencies(radarrHandler, sonarrHandler);
        TvUploadRecord staged = await deps.Store.StageAsync(
            Content(), "Re.Zero.S01E01.1080p.mkv", 16, batch, "episode", "tv");

        string destination = Path.Combine(deps.Root, "media", "tv", "shows", "10", "Season 1", "Re.Zero.S01E01.mkv");
        int episodeGets = 0;
        sonarrHandler.Fallback = (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v3/series/10")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(SeriesJson));
            if (request.Method == HttpMethod.Get && path == "/api/v3/episode")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(EpisodesJson(episodeGets++ > 0)));
            if (request.Method == HttpMethod.Get && path == "/api/v3/manualimport")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(EpisodeSuggestion(staged.ContainerPath)));
            if (request.Method == HttpMethod.Post && path == "/api/v3/manualimport")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(EpisodeSuggestion(staged.ContainerPath)));
            if (request.Method == HttpMethod.Post && path == "/api/v3/command")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse("""{ "id": 88, "name": "ManualImport", "status": "queued" }"""));
            if (request.Method == HttpMethod.Get && path == "/api/v3/command/88")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse("""{ "id": 88, "name": "ManualImport", "status": "completed", "result": "successful", "message": "Manually imported 1 files" }"""));
            if (request.Method == HttpMethod.Get && path == "/api/v3/episodefile")
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(EpisodeFile(destination)));
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse("[]"));
        };
        radarrHandler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("[]"));

        TvImportService service = BuildService(deps, deps.Root);
        TvImportResult result = await service.ImportAsync(new TvImportRequest
        {
            BatchId = batch,
            MediaType = "tv",
            LibraryId = 10,
            TvdbId = 305089,
            Title = "Re: ZERO, Starting Life in Another World",
            Mappings = [new TvImportFileMapping(staged.Id, 1, 1, EpisodeId: 610)]
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Files.Single().Status).IsEqualTo(TvUploadStatus.Imported);
        await Assert.That(File.Exists(staged.StoredPath)).IsFalse();

        CapturedRequest command = sonarrHandler.Requests.Single(request =>
            request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/v3/command");
        await Assert.That(command.Body).Contains("\"seriesId\":10");
        await Assert.That(command.Body).Contains("\"episodeIds\":[610]");
        await Assert.That(command.Body).Contains("\"seasonNumber\":1");
        await Assert.That(command.Body).Contains("Bluray-1080p");

        List<TvUploadRecord> rows = await deps.Store.GetBatchAsync(batch);
        await Assert.That(rows.Single().Status).IsEqualTo(TvUploadStatus.Imported);
    }
}
