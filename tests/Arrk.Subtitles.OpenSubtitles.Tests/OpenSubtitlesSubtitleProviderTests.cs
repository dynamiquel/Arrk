using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Events;
using Arrk.Media.Abstractions;
using Arrk.Subtitles;
using Arrk.Subtitles.OpenSubtitles;
using Arrk.TestSupport;
using Arrk.Tv.Abstractions;

namespace Arrk.Subtitles.OpenSubtitles.Tests;

public class OpenSubtitlesSubtitleProviderTests
{
    const string SearchPayload = """
    {
      "data": [
        {
          "id": "1",
          "attributes": {
            "language": "en",
            "download_count": 1200,
            "hearing_impaired": false,
            "foreign_parts_only": false,
            "moviehash_match": true,
            "format": "srt",
            "release": "Verity.2026.1080p.WEB-DL",
            "ratings": 8.4,
            "uploader": { "name": "someone" },
            "files": [ { "file_id": 12345, "file_name": "Verity.2026.1080p.srt" } ]
          }
        },
        {
          "id": "2",
          "attributes": {
            "language": "fr",
            "download_count": 200,
            "hearing_impaired": false,
            "foreign_parts_only": false,
            "format": "srt",
            "release": "Verity.2026.1080p.WEB-DL",
            "ratings": 7.1,
            "files": [ { "file_id": 22222, "file_name": "Verity.2026.1080p.fr.srt" } ]
          }
        }
      ]
    }
    """;

    [Test]
    public async Task Provider_RequiresAnApiKeyToBeEnabled()
    {
        string directory = CreateTempDirectory();
        try
        {
            OpenSubtitlesSubtitleProvider withoutKey = Provider(
                directory,
                new FakeHttpMessageHandler(),
                apiKey: null);
            OpenSubtitlesSubtitleProvider withKey = Provider(directory, new FakeHttpMessageHandler());

            await Assert.That(withoutKey.IsEnabled).IsFalse();
            await Assert.That(withKey.IsEnabled).IsTrue();
            await Assert.That(withKey.Priority).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Search_MapsCandidatesAndFiltersToRequestedLanguages()
    {
        string directory = CreateTempDirectory();
        try
        {
            await CreateVideoAsync(directory);
            FakeHttpMessageHandler handler = new();
            handler.EnqueueJson(SearchPayload);

            OpenSubtitlesSubtitleProvider provider = Provider(directory, handler);
            IReadOnlyList<SubtitleCandidate> candidates = await provider.SearchAsync(
                new SubtitleSearchRequest(SubtitleTarget.Movie(5, "Verity"), ["fr"]));

            await Assert.That(candidates).HasSingleItem();
            await Assert.That(candidates[0].Provider).IsEqualTo("opensubtitles");
            await Assert.That(candidates[0].Id).IsEqualTo("22222");
            await Assert.That(candidates[0].LanguageCode).IsEqualTo("fr");
            await Assert.That(candidates[0].Score).IsGreaterThan(0);

            CapturedRequest request = handler.Requests.Single();
            await Assert.That(request.Uri.Query).Contains("imdb_id=1234567");
            await Assert.That(request.Uri.Query).Contains("type=movie");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Download_WritesSidecarAndPublishesEvent()
    {
        string directory = CreateTempDirectory();
        try
        {
            string video = await CreateVideoAsync(directory);
            FakeHttpMessageHandler handler = new();
            handler.EnqueueJson("""
            {
              "link": "https://dl.example/abc",
              "file_name": "Verity.2026.1080p.fr.srt",
              "remaining": 19,
              "requests": 1
            }
            """);
            handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nBonjour\n"))
            }));

            EventBus bus = new();
            List<SubtitleDownloadedEvent> events = [];
            using IDisposable subscription = bus.Subscribe<SubtitleDownloadedEvent>((evt, _) =>
            {
                events.Add(evt);
                return ValueTask.CompletedTask;
            });

            OpenSubtitlesSubtitleProvider provider = Provider(directory, handler, bus);
            await provider.DownloadAsync(new SubtitleDownloadRequest(
                SubtitleTarget.Movie(5, "Verity"),
                new SubtitleCandidate
                {
                    Provider = "opensubtitles",
                    Id = "22222",
                    LanguageCode = "fr",
                    LanguageName = "French"
                }));

            string sidecar = Path.Combine(directory, "Verity.2026.fr.srt");
            await Assert.That(File.Exists(sidecar)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(sidecar)).Contains("Bonjour");
            await Assert.That(events).HasCount(1);
            await Assert.That(events[0].Language).IsEqualTo("fr");
            await Assert.That(events[0].MovieId).IsEqualTo(5);
            await Assert.That(video).IsNotNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static OpenSubtitlesSubtitleProvider Provider(
        string directory,
        FakeHttpMessageHandler handler,
        IEventBus? bus = null,
        string? apiKey = "api-key")
    {
        OpenSubtitlesConfig config = new() { ApiKey = apiKey };
        OpenSubtitlesClient client = new(new MutableOptionsMonitor<OpenSubtitlesConfig>(config), handler);
        SubtitleFileService files = new(
            [new FakePathMapper("/movies", directory)],
            []);
        FakeMovieLibrary movies = new(Movie(directory));
        FakeSeriesLibrary series = new();
        SubtitleLibraryResolver library = new(
            [movies],
            [series],
            NullLogger<SubtitleLibraryResolver>.Instance);

        return new OpenSubtitlesSubtitleProvider(
            client,
            files,
            library,
            new MutableOptionsMonitor<OpenSubtitlesConfig>(config),
            bus ?? new EventBus(),
            NullLogger<OpenSubtitlesSubtitleProvider>.Instance);
    }

    static async Task<string> CreateVideoAsync(string directory)
    {
        string video = Path.Combine(directory, "Verity.2026.mkv");
        await File.WriteAllBytesAsync(video, new byte[256]);
        return video;
    }

    static TvMovie Movie(string directory) => new()
    {
        Id = 5,
        Title = "Verity",
        Year = 2026,
        ImdbId = "tt1234567",
        TmdbId = 42,
        HasFile = true,
        File = new MediaFileInfo
        {
            Path = "/movies/Verity.2026.mkv",
            SizeBytes = 256,
            DateAdded = DateTime.UtcNow
        }
    };

    static string CreateTempDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "stackarr-provider-" + Guid.NewGuid().ToString("N"))).FullName;

    sealed class FakePathMapper(string container, string local) : IMediaPathMapper
    {
        public string? MapToLocalPath(string arrPath)
        {
            if (!arrPath.StartsWith(container + "/", StringComparison.Ordinal))
                return null;

            return Path.Combine(local, arrPath[(container.Length + 1)..]);
        }
    }

    sealed class FakeMovieLibrary(TvMovie movie) : IMovieLibrary
    {
        public Task<IReadOnlyList<TvMovie>> GetAllMoviesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TvMovie>>([movie]);

        public Task<TvMovie?> GetMovieAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<TvMovie?>(id == movie.Id ? movie : null);
    }

    sealed class FakeSeriesLibrary : ISeriesLibrary
    {
        public Task<IReadOnlyList<TvSeries>> GetAllSeriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TvSeries>>([]);

        public Task<TvSeries?> GetSeriesAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<TvSeries?>(null);

        public Task<IReadOnlyList<TvSeason>> GetSeasonsAsync(int seriesId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TvSeason>>([]);

        public Task<IReadOnlyList<TvEpisode>> GetEpisodesAsync(int seriesId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TvEpisode>>([]);
    }
}
