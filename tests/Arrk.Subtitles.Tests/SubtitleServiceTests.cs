using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Subtitles;
using Arrk.Core.Events;
using Arrk.Media.Abstractions;
using Arrk.Subtitles;
using Arrk.TestSupport;
using Arrk.Tv.Abstractions;

namespace Arrk.Subtitles.Tests;

public class SubtitleServiceTests
{
    [Test]
    public async Task UsesAllEnabledProvidersAndPrefersTheLowestPriorityForOrdering()
    {
        FakeProvider openSubtitles = new("opensubtitles", priority: 0, enabled: true);
        FakeProvider bazarr = new("bazarr", priority: 1, enabled: true);

        SubtitleService service = Service([bazarr, openSubtitles], ["en"]);

        await Assert.That(service.IsAvailable).IsTrue();
        await Assert.That(service.ProviderName).IsEqualTo("opensubtitles, bazarr");
        await Assert.That(service.DefaultLanguages).IsEquivalentTo(["en"]);
    }

    [Test]
    public async Task ExcludesDisabledProviders()
    {
        FakeProvider openSubtitles = new("opensubtitles", priority: 0, enabled: false);
        FakeProvider bazarr = new("bazarr", priority: 1, enabled: true);

        SubtitleService service = Service([openSubtitles, bazarr], ["fr"]);

        await Assert.That(service.IsAvailable).IsTrue();
        await Assert.That(service.ProviderName).IsEqualTo("bazarr");
        await Assert.That(service.DefaultLanguages).IsEquivalentTo(["fr"]);
    }

    [Test]
    public async Task IsUnavailableWhenNoProviderIsEnabled()
    {
        SubtitleService service = Service(
            [new FakeProvider("opensubtitles", 0, false)],
            ["en"]);

        await Assert.That(service.IsAvailable).IsFalse();
        await Assert.That(service.DefaultLanguages).IsEquivalentTo(["en"]);
        await Assert.That(service.ProviderName).IsEqualTo("");
    }

    [Test]
    public async Task PreferencesAreNormalizedFromSharedConfig()
    {
        SubtitleService service = Service(
            [new FakeProvider("opensubtitles", 0, true)],
            ["fr", "EN", "fr"]);

        await Assert.That(service.Preferences.Languages).IsEquivalentTo(["fr", "en"]);
        await Assert.That(service.Preferences.AutoDownload).IsTrue();
    }

    [Test]
    public async Task Search_MergesCandidatesFromEveryProvider()
    {
        FakeProvider first = new("opensubtitles", 0, true)
        {
            Candidates =
            [
                new SubtitleCandidate { Provider = "opensubtitles", Id = "1", LanguageCode = "en", Score = 90 }
            ]
        };
        FakeProvider second = new("bazarr", 1, true)
        {
            Candidates =
            [
                new SubtitleCandidate { Provider = "bazarr", Id = "2", LanguageCode = "en", Score = 95 }
            ]
        };

        SubtitleService service = Service([first, second], ["en"]);
        IReadOnlyList<SubtitleCandidate> candidates = await service.SearchAsync(SubtitleTarget.Movie(1), ["en"]);

        await Assert.That(candidates).HasCount(2);
        await Assert.That(candidates[0].Provider).IsEqualTo("bazarr");
        await Assert.That(candidates[1].Provider).IsEqualTo("opensubtitles");
    }

    [Test]
    public async Task Download_DispatchesToTheProviderNamedOnTheCandidate()
    {
        FakeProvider openSubtitles = new("opensubtitles", 0, true);
        FakeProvider bazarr = new("bazarr", 1, true);
        SubtitleService service = Service([openSubtitles, bazarr], ["en"]);

        await service.DownloadAsync(
            SubtitleTarget.Movie(1),
            new SubtitleCandidate { Provider = "bazarr", Id = "abc", LanguageCode = "en" });

        await Assert.That(bazarr.Downloads).IsEqualTo(1);
        await Assert.That(openSubtitles.Downloads).IsEqualTo(0);
    }

    [Test]
    public async Task ImportWatcher_RequestsSubtitlesForTheImportedMovie()
    {
        string directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "stackarr-import-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "Verity.2026.mkv"), new byte[256]);
            FakeProvider provider = new("opensubtitles", 0, true)
            {
                Candidates =
                [
                    new SubtitleCandidate
                    {
                        Provider = "opensubtitles",
                        Id = "1",
                        LanguageCode = "en",
                        Score = 90
                    }
                ]
            };
            TvMovie movie = new()
            {
                Id = 5,
                Title = "Verity",
                HasFile = true,
                File = new MediaFileInfo
                {
                    Path = "/movies/Verity.2026.mkv",
                    SizeBytes = 256,
                    DateAdded = DateTime.UtcNow
                }
            };

            SubtitleService service = Service([provider], ["en"], movie, directory);
            MutableOptionsMonitor<SubtitlesConfig> config = new(new SubtitlesConfig());
            SubtitleLibraryResolver library = new(
                [new FakeMovieLibrary(movie)],
                [],
                NullLogger<SubtitleLibraryResolver>.Instance);
            SubtitleAutoDownloader downloader = new(
                service,
                config,
                library,
                NullLogger<SubtitleAutoDownloader>.Instance);

            await downloader.HandleImportAsync(
                new MediaImportedEvent("test", "Verity", "Movie", MovieId: 5),
                CancellationToken.None);

            await Assert.That(provider.Downloads).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Request_DownloadsTheBestMissingCandidate()
    {
        string directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "stackarr-dispatch-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "Verity.2026.mkv"), new byte[256]);
            FakeProvider provider = new("opensubtitles", 0, true)
            {
                Candidates =
                [
                    new SubtitleCandidate
                    {
                        Provider = "opensubtitles",
                        Id = "1",
                        LanguageCode = "en",
                        Score = 90
                    }
                ]
            };
            TvMovie movie = new()
            {
                Id = 5,
                Title = "Verity",
                HasFile = true,
                File = new MediaFileInfo
                {
                    Path = "/movies/Verity.2026.mkv",
                    SizeBytes = 256,
                    DateAdded = DateTime.UtcNow
                }
            };

            SubtitleService service = Service([provider], ["en"], movie, directory);
            await service.RequestAsync(SubtitleTarget.Movie(5));

            await Assert.That(provider.Downloads).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static SubtitleService Service(
        IEnumerable<ISubtitleProvider> providers,
        IEnumerable<string> languages,
        TvMovie? movie = null,
        string? localRoot = null)
    {
        MutableOptionsMonitor<SubtitlesConfig> config = new(new SubtitlesConfig
        {
            Languages = [.. languages]
        });
        SubtitleFileService files = new(
            localRoot is null ? [] : [new FakePathMapper("/movies", localRoot)],
            []);
        SubtitleLibraryResolver library = new(
            movie is null ? [] : [new FakeMovieLibrary(movie)],
            [],
            NullLogger<SubtitleLibraryResolver>.Instance);
        SubtitleStatusService status = new(files, library, config);
        return new SubtitleService(
            providers,
            config,
            status,
            library,
            NullLogger<SubtitleService>.Instance);
    }

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

    sealed class FakeProvider(string name, int priority, bool enabled) : ISubtitleProvider
    {
        public List<SubtitleCandidate> Candidates { get; init; } = [];

        public int Downloads { get; private set; }

        public string Name => name;

        public int Priority => priority;

        public bool IsEnabled => enabled;

        public Task ConfigureAsync(SubtitlePreferences preferences, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(enabled);

        public Task<IReadOnlyList<SubtitleLanguage>> GetLanguagesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubtitleLanguage>>([]);

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
            SubtitleSearchRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubtitleCandidate>>(Candidates);

        public Task DownloadAsync(SubtitleDownloadRequest request, CancellationToken ct = default)
        {
            Downloads++;
            return Task.CompletedTask;
        }
    }
}
