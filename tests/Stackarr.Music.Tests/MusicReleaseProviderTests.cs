using Stackarr.Music.Services;
using Stackarr.Core.Models;

namespace Stackarr.Music.Tests;

/// <summary>
/// Hermetic tests for the music provider's candidate mapping (no network). Verifies that a scored
/// Prowlarr music release maps onto the shared picker shape with the grab keys MusicGrabAsync needs.
/// </summary>
public class MusicReleaseProviderTests
{
    [Test]
    public async Task ToCandidate_MapsMusicFieldsAndGrabKeys()
    {
        MusicRelease release = new()
        {
            Title = "Halo 3 ODST (Original Soundtrack) [FLAC]",
            Indexer = "The Pirate Bay",
            Size = 620_000_000,
            Seeders = 75,
            InfoHash = "DEADBEEF",
            MagnetUrl = "magnet:?xt=urn:btih:DEADBEEF",
            DownloadUrl = "http://prowlarr/download",
            Protocol = "torrent",
            PublishDate = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            IsLossless = true,
            Score = 92
        };

        ReleaseCandidate candidate = MusicReleaseProvider.ToCandidate(release);

        await Assert.That(candidate.Id).IsEqualTo("DEADBEEF");
        await Assert.That(candidate.Title).IsEqualTo(release.Title);
        await Assert.That(candidate.Indexer).IsEqualTo("The Pirate Bay");
        await Assert.That(candidate.Size).IsEqualTo(620_000_000L);
        await Assert.That(candidate.Seeders).IsEqualTo(75);
        await Assert.That(candidate.Score).IsEqualTo(92);
        await Assert.That(candidate.QualityLabel).IsEqualTo("Lossless");
        await Assert.That(candidate.IsLossless).IsTrue();
        await Assert.That(candidate.MagnetUrl).IsEqualTo("magnet:?xt=urn:btih:DEADBEEF");
        await Assert.That(candidate.InfoHash).IsEqualTo("DEADBEEF");
        await Assert.That(candidate.PublishDate).IsEqualTo(release.PublishDate);
    }

    [Test]
    public async Task ToCandidate_FallsBackToTitleWhenNoHash()
    {
        ReleaseCandidate candidate = MusicReleaseProvider.ToCandidate(new MusicRelease { Title = "No Hash Album" });

        await Assert.That(candidate.Id).IsEqualTo("No Hash Album");
        await Assert.That(candidate.QualityLabel).IsEqualTo("Lossy");
        await Assert.That(candidate.IsLossless).IsFalse();
    }
}
