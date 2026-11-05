using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Media.Abstractions;
using Stackarr.Subtitles;
using Stackarr.TestSupport;
using Stackarr.Tv.Abstractions;

namespace Stackarr.Subtitles.Tests;

public class SubtitleStatusServiceTests
{
    [Test]
    public async Task Status_CombinesSidecarsWithConfiguredLanguages()
    {
        string directory = CreateTempDirectory();
        try
        {
            string video = Path.Combine(directory, "Verity.2026.mkv");
            await File.WriteAllBytesAsync(video, new byte[256]);
            await File.WriteAllTextAsync(Path.Combine(directory, "Verity.2026.en.srt"), "1");

            SubtitleStatusService status = Service(directory, ["en", "fr"]);
            SubtitleStatus? result = await status.GetStatusAsync(SubtitleTarget.Movie(5, "Verity"));

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Available.Select(track => track.LanguageCode)).Contains("en");
            await Assert.That(result.Missing.Select(language => language.Code)).Contains("fr");
            await Assert.That(result.MissingEpisodeCount).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Status_NormalizesBibliographicLanguageCodes()
    {
        string directory = CreateTempDirectory();
        try
        {
            string video = Path.Combine(directory, "Verity.2026.mkv");
            await File.WriteAllBytesAsync(video, new byte[256]);
            await File.WriteAllTextAsync(Path.Combine(directory, "Verity.2026.fre.srt"), "1");

            SubtitleStatusService status = Service(directory, ["fr"]);
            SubtitleStatus? result = await status.GetStatusAsync(SubtitleTarget.Movie(5, "Verity"));

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Available.Single().LanguageCode).IsEqualTo("fr");
            await Assert.That(result.Missing).IsEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static SubtitleStatusService Service(string directory, IReadOnlyList<string> languages)
    {
        MutableOptionsMonitor<SubtitlesConfig> config = new(new SubtitlesConfig { Languages = [.. languages] });
        SubtitleFileService files = new([new FakePathMapper("/movies", directory)], []);
        TvMovie movie = new()
        {
            Id = 5,
            Title = "Verity",
            Year = 2026,
            HasFile = true,
            File = new MediaFileInfo
            {
                Path = "/movies/Verity.2026.mkv",
                SizeBytes = 256,
                DateAdded = DateTime.UtcNow
            }
        };
        SubtitleLibraryResolver library = new(
            [new FakeMovieLibrary(movie)],
            [],
            NullLogger<SubtitleLibraryResolver>.Instance);
        return new SubtitleStatusService(files, library, config);
    }

    static string CreateTempDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "stackarr-status-" + Guid.NewGuid().ToString("N"))).FullName;

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
}
