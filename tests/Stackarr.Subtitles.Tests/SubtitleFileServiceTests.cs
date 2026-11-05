using System.IO.Compression;
using System.Text;
using Stackarr.Media.Abstractions;
using Stackarr.Subtitles;

namespace Stackarr.Subtitles.Tests;

public class SubtitleFileServiceTests
{
    [Test]
    public async Task GetTracks_ReadsSidecarsWithLanguageAndFlags()
    {
        string directory = CreateTempDirectory();
        try
        {
            string video = Path.Combine(directory, "Verity.2026.mkv");
            await File.WriteAllBytesAsync(video, new byte[256]);
            await File.WriteAllTextAsync(Path.Combine(directory, "Verity.2026.en.srt"), "1");
            await File.WriteAllTextAsync(Path.Combine(directory, "Verity.2026.fr.forced.srt"), "1");
            await File.WriteAllTextAsync(Path.Combine(directory, "Verity.2026.de.hi.srt"), "1");
            await File.WriteAllTextAsync(Path.Combine(directory, "Verity.2026.pt-BR.vtt"), "WEBVTT");

            SubtitleFileService service = Service(directory);
            IReadOnlyList<SubtitleTrack> tracks = await service.GetTracksAsync("/movies/Verity.2026.mkv", null);

            await Assert.That(tracks.Select(track => track.LanguageCode)).Contains("en");
            await Assert.That(tracks.Select(track => track.LanguageCode)).Contains("fr");
            await Assert.That(tracks.Select(track => track.LanguageCode)).Contains("de");
            await Assert.That(tracks.Select(track => track.LanguageCode)).Contains("pt");
            await Assert.That(tracks.Single(track => track.LanguageCode == "fr").Forced).IsTrue();
            await Assert.That(tracks.Single(track => track.LanguageCode == "de").HearingImpaired).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Write_CreatesJellyfinCompatibleSidecar()
    {
        string directory = CreateTempDirectory();
        try
        {
            string video = Path.Combine(directory, "Verity.2026.mkv");
            await File.WriteAllBytesAsync(video, new byte[256]);
            SubtitleFileService service = Service(directory);
            byte[] subtitle = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHello\n");

            string path = await service.WriteAsync(
                "/movies/Verity.2026.mkv",
                "fr",
                forced: true,
                hearingImpaired: true,
                subtitle,
                "Verity.2026.fr.srt",
                overwrite: true);

            await Assert.That(Path.GetFileName(path)).IsEqualTo("Verity.2026.fr.forced.hi.srt");
            await Assert.That(await File.ReadAllTextAsync(path)).Contains("Hello");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Write_ExtractsTheSubtitleFromAZip()
    {
        string directory = CreateTempDirectory();
        try
        {
            string video = Path.Combine(directory, "Verity.2026.mkv");
            await File.WriteAllBytesAsync(video, new byte[256]);

            byte[] zip;
            using (MemoryStream stream = new())
            {
                using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    ZipArchiveEntry entry = archive.CreateEntry("Verity.2026.fr.srt");
                    await using Stream writer = entry.Open();
                    await writer.WriteAsync(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nBonjour\n"));
                }
                zip = stream.ToArray();
            }

            SubtitleFileService service = Service(directory);
            string path = await service.WriteAsync(
                "/movies/Verity.2026.mkv",
                "fr",
                forced: false,
                hearingImpaired: false,
                zip,
                "download.zip",
                overwrite: true);

            await Assert.That(Path.GetExtension(path)).IsEqualTo(".srt");
            await Assert.That(await File.ReadAllTextAsync(path)).Contains("Bonjour");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ComputeHash_IsStableAndSixteenHexCharacters()
    {
        string directory = CreateTempDirectory();
        try
        {
            string video = Path.Combine(directory, "large.mkv");
            byte[] payload = new byte[256 * 1024];
            Random.Shared.NextBytes(payload);
            await File.WriteAllBytesAsync(video, payload);

            SubtitleFileService service = Service(directory);
            string? first = await service.ComputeHashAsync("/movies/large.mkv");
            string? second = await service.ComputeHashAsync("/movies/large.mkv");

            await Assert.That(first).IsNotNull();
            await Assert.That(first!.Length).IsEqualTo(16);
            await Assert.That(second).IsEqualTo(first);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task MissingVideo_ThrowsAClearError()
    {
        string directory = CreateTempDirectory();
        try
        {
            SubtitleFileService service = Service(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(
                "/movies/missing.mkv",
                "en",
                false,
                false,
                [1, 2, 3],
                "missing.srt",
                overwrite: true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static SubtitleFileService Service(string localRoot) =>
        new([new FakePathMapper("/movies", localRoot)], []);

    static string CreateTempDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "stackarr-subtitles-" + Guid.NewGuid().ToString("N"))).FullName;

    sealed class FakePathMapper(string container, string local) : IMediaPathMapper
    {
        public string? MapToLocalPath(string arrPath)
        {
            if (!arrPath.StartsWith(container + "/", StringComparison.Ordinal))
                return null;

            return Path.Combine(local, arrPath[(container.Length + 1)..]);
        }
    }
}
