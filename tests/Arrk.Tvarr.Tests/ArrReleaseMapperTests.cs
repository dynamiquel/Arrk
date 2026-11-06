using Arrk.Core.Models;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Hermetic tests for the arr release → shared candidate mapping and the format chips the
/// interactive-search picker shows. No HTTP: the mapper is pure, so these pin exactly what the
/// UI renders for a given release name / resource.
/// </summary>
public class ArrReleaseMapperTests
{
    [Test]
    public async Task BuildTags_DetectsPreferredVideoFormats()
    {
        IReadOnlyList<string> tags = ArrReleaseMapper.BuildTags(
            "Scary.Movie.2026.2160p.UHD.BluRay.REMUX.DV.P7.HDR.MULTi.H265-BTM");

        await Assert.That(tags).Contains("Dolby Vision");
        await Assert.That(tags).Contains("HDR");
        await Assert.That(tags).Contains("x265");
        await Assert.That(tags).Contains("REMUX");
        await Assert.That(tags).Contains("BluRay");
    }

    [Test]
    public async Task BuildTags_PrefersMostSpecificHdrAndAudio()
    {
        IReadOnlyList<string> tags = ArrReleaseMapper.BuildTags(
            "Dune.Part.Two.2024.2160p.WEB-DL.HDR10+.DDP5.1.Atmos.HDR.x265-GROUP");

        await Assert.That(tags).Contains("HDR10+");
        await Assert.That(tags).DoesNotContain("HDR10");
        await Assert.That(tags).DoesNotContain("HDR");
        await Assert.That(tags).Contains("Atmos");
        await Assert.That(tags).Contains("DD+");
        await Assert.That(tags).Contains("WEB-DL");
    }

    [Test]
    public async Task BuildTags_RecognisesDtsHdAndX264()
    {
        IReadOnlyList<string> tags = ArrReleaseMapper.BuildTags(
            "Some.Show.S01E01.1080p.BluRay.x264.DTS-HD.MA.5.1-GROUP");

        await Assert.That(tags).Contains("x264");
        await Assert.That(tags).Contains("DTS-HD");
        await Assert.That(tags).DoesNotContain("DTS");
        await Assert.That(tags).Contains("BluRay");
    }

    [Test]
    public async Task BuildTags_EmptyForPlainName()
    {
        IReadOnlyList<string> tags = ArrReleaseMapper.BuildTags("Some Release 2024");
        await Assert.That(tags).IsEmpty();
    }

    [Test]
    public async Task ToCandidate_MapsCoreFieldsAndGrabKeys()
    {
        ReleaseResourceDto dto = new()
        {
            Guid = "magnet:?xt=urn:btih:ABC",
            Title = "The.Matrix.1999.2160p.WEB-DL.DV.HDR.x265.Atmos-GROUP",
            IndexerId = 7,
            Indexer = "The Pirate Bay",
            Size = 18_400_000_000,
            Seeders = 42,
            Leechers = 3,
            Protocol = "torrent",
            PublishDate = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            CustomFormatScore = 180,
            Rejected = true,
            Rejections = ["Remux-2160p is not wanted in profile"],
            InfoHash = "ABC",
            DownloadUrl = "http://prowlarr/download",
            Quality = new FileQualityDto { Quality = new QualityDto { Name = "WEBDL-2160p" } }
        };

        ReleaseCandidate candidate = ArrReleaseMapper.ToCandidate(dto);

        await Assert.That(candidate.Id).IsEqualTo("magnet:?xt=urn:btih:ABC");
        await Assert.That(candidate.Title).IsEqualTo(dto.Title);
        await Assert.That(candidate.Guid).IsEqualTo("magnet:?xt=urn:btih:ABC");
        await Assert.That(candidate.IndexerId).IsEqualTo(7);
        await Assert.That(candidate.Indexer).IsEqualTo("The Pirate Bay");
        await Assert.That(candidate.Size).IsEqualTo(18_400_000_000L);
        await Assert.That(candidate.Seeders).IsEqualTo(42);
        await Assert.That(candidate.Score).IsEqualTo(180);
        await Assert.That(candidate.QualityLabel).IsEqualTo("WEBDL-2160p");
        await Assert.That(candidate.Rejected).IsTrue();
        await Assert.That(candidate.Rejections).Count().IsEqualTo(1);
        await Assert.That(candidate.IsUsenet).IsFalse();
        await Assert.That(candidate.FormatTags).Contains("Dolby Vision");
        await Assert.That(candidate.FormatTags).Contains("HDR");
        await Assert.That(candidate.FormatTags).Contains("Atmos");
    }

    [Test]
    public async Task ToCandidate_FallsBackToTitleWhenGuidMissing()
    {
        ReleaseCandidate candidate = ArrReleaseMapper.ToCandidate(new ReleaseResourceDto
        {
            Title = "No Guid Release",
            Protocol = "usenet"
        });

        await Assert.That(candidate.Id).IsEqualTo("No Guid Release");
        await Assert.That(candidate.IsUsenet).IsTrue();
    }
}
