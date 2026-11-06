using Arrk.Tvarr.Models;
using Arrk.Media.Abstractions;
using static Arrk.Media.Abstractions.Resolutions;
using static Arrk.Media.Abstractions.VideoCodecs;
using static Arrk.Media.Abstractions.AudioCodecs;
using QualitySources = Arrk.Media.Abstractions.QualitySources;

namespace Arrk.Orchestration.Tests;

public class QualityInfoTests
{
    [Test]
    public async Task FromRadarrQuality_With4KBluRayDV_ReturnsCorrectDisplayText()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Remux-2160p",
            resolution: R2160p,
            videoCodec: x265,
            audioCodec: TrueHD,
            hdrType: HdrTypes.DolbyVision,
            source: QualitySources.Bluray);

        await Assert.That(quality.Resolution).IsEqualTo(R2160p);
        await Assert.That(quality.Source).IsEqualTo(QualitySource.Bluray);
        await Assert.That(quality.VideoCodec).IsEqualTo(x265);
        await Assert.That(quality.AudioCodec).IsEqualTo(TrueHD);
        await Assert.That(quality.HDR).IsEqualTo(QualityHDR.DolbyVision);
        await Assert.That(quality.DisplayText).Contains("4K");
        await Assert.That(quality.DisplayText).Contains("Bluray");
        await Assert.That(quality.DisplayText).Contains("DV");
        await Assert.That(quality.DisplayText).Contains(TrueHD);
    }

    [Test]
    public async Task FromRadarrQuality_With1080pWebHDR10Plus_ReturnsCorrectDisplayText()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "WEBDL-1080p",
            resolution: R1080p,
            videoCodec: x264,
            audioCodec: AAC,
            hdrType: HdrTypes.HDR10Plus,
            source: QualitySources.Web);

        await Assert.That(quality.Resolution).IsEqualTo(R1080p);
        await Assert.That(quality.Source).IsEqualTo(QualitySource.Web);
        await Assert.That(quality.VideoCodec).IsEqualTo(x264);
        await Assert.That(quality.AudioCodec).IsEqualTo(AAC);
        await Assert.That(quality.HDR).IsEqualTo(QualityHDR.HDR10Plus);
        await Assert.That(quality.DisplayText).Contains(R1080p);
        await Assert.That(quality.DisplayText).Contains("Web");
        await Assert.That(quality.DisplayText).Contains("HDR10+");
    }

    [Test]
    public async Task FromRadarrQuality_With720pNoHdr_ReturnsNoneHDR()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "HDTV-720p",
            resolution: R720p,
            videoCodec: x264,
            audioCodec: AAC,
            hdrType: "",
            source: QualitySources.HDTV);

        await Assert.That(quality.Resolution).IsEqualTo(R720p);
        await Assert.That(quality.Source).IsEqualTo(QualitySource.HDTV);
        await Assert.That(quality.HDR).IsEqualTo(QualityHDR.None);
    }

    [Test]
    public async Task FromRadarrQuality_EmptyInputs_UsesQualityNameToInfer()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(qualityName: "Bluray-2160p");

        await Assert.That(quality.Resolution).IsEqualTo(R2160p);
        await Assert.That(quality.Source).IsEqualTo(QualitySource.Bluray);
    }

    [Test]
    public async Task FromRadarrQuality_WithHDR10_ReturnsHDR10()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Remux-2160p",
            hdrType: HdrTypes.HDR10);

        await Assert.That(quality.HDR).IsEqualTo(QualityHDR.HDR10);
    }

    [Test]
    public async Task FromRadarrQuality_DolbyVisionWithHDR10Plus_ReturnsHDR10PlusDV()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Remux-2160p",
            hdrType: $"{HdrTypes.DolbyVision} {HdrTypes.HDR10Plus}");

        await Assert.That(quality.HDR).IsEqualTo(QualityHDR.HDR10PlusDV);
    }

    [Test]
    public async Task FromRadarrQuality_UnknownSource_ReturnsUnknown()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Unknown-1080p",
            source: "");

        await Assert.That(quality.Source).IsEqualTo(QualitySource.Unknown);
    }

    [Test]
    public async Task FromRadarrQuality_RemuxSource_ReturnsRemux()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Remux-2160p",
            source: QualitySources.Remux);

        await Assert.That(quality.Source).IsEqualTo(QualitySource.Remux);
    }

    [Test]
    public async Task FromRadarrQuality_DVDSource_ReturnsDVD()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "DVD-480p",
            source: QualitySources.DVD);

        await Assert.That(quality.Source).IsEqualTo(QualitySource.DVD);
    }

    [Test]
    public async Task FromRadarrQuality_VideoCodecHevc_NormalizesToX265()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Bluray-2160p",
            videoCodec: "HEVC");

        await Assert.That(quality.VideoCodec).IsEqualTo(x265);
    }

    [Test]
    public async Task FromRadarrQuality_VideoCodecH264_NormalizesToX264()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Bluray-1080p",
            videoCodec: "H264");

        await Assert.That(quality.VideoCodec).IsEqualTo(x264);
    }

    [Test]
    public async Task FromRadarrQuality_VideoCodecAV1_ReturnsAV1()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "WEBDL-1080p",
            videoCodec: AV1);

        await Assert.That(quality.VideoCodec).IsEqualTo(AV1);
    }

    [Test]
    public async Task FromRadarrQuality_AudioCodecAtmos_ReturnsAtmos()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Bluray-2160p",
            audioCodec: "Dolby Atmos");

        await Assert.That(quality.AudioCodec).IsEqualTo(Atmos);
    }

    [Test]
    public async Task FromRadarrQuality_AudioCodecDtsx_ReturnsDtsX()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Bluray-2160p",
            audioCodec: DTSX);

        await Assert.That(quality.AudioCodec).IsEqualTo(DTSX);
    }

    [Test]
    public async Task FromRadarrQuality_AudioCodecDtsHdMa_ReturnsDtsHdMa()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Bluray-1080p",
            audioCodec: DTSHDMA);

        await Assert.That(quality.AudioCodec).IsEqualTo(DTSHDMA);
    }

    [Test]
    public async Task FromRadarrQuality_AudioCodecFlac_ReturnsFlac()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Bluray-1080p",
            audioCodec: FLAC);

        await Assert.That(quality.AudioCodec).IsEqualTo(FLAC);
    }

    [Test]
    public async Task FromRadarrQuality_AudioCodecOpus_ReturnsOpus()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "WEBDL-1080p",
            audioCodec: Opus);

        await Assert.That(quality.AudioCodec).IsEqualTo(Opus);
    }

    [Test]
    public async Task FromRadarrQuality_AudioCodecAc3_ReturnsAC3()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Bluray-1080p",
            audioCodec: AC3);

        await Assert.That(quality.AudioCodec).IsEqualTo(AC3);
    }

    [Test]
    public async Task FromRadarrQuality_Resolution480_Returns480p()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "DVD-480p",
            resolution: R480p);

        await Assert.That(quality.Resolution).IsEqualTo(R480p);
    }

    [Test]
    public async Task FromRadarrQuality_DisplayText_SuppressesHDR10WhenDVPresent()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Bluray-2160p",
            hdrType: $"{HdrTypes.DolbyVision} {HdrTypes.HDR10}",
            source: QualitySources.Bluray);

        await Assert.That(quality.HDR).IsEqualTo(QualityHDR.DolbyVision);
        await Assert.That(quality.DisplayText).Contains("DV");
        await Assert.That(quality.DisplayText).DoesNotContain("HDR10");
    }

    [Test]
    public async Task FromSonarrQuality_DelegatesToFromRadarrQuality()
    {
        QualityInfo quality = ArrQualityMapper.FromSonarrQuality(
            qualityName: "WEBDL-1080p",
            resolution: R1080p,
            source: QualitySources.Web);

        await Assert.That(quality.Resolution).IsEqualTo(R1080p);
        await Assert.That(quality.Source).IsEqualTo(QualitySource.Web);
    }

    [Test]
    public async Task FromRadarrQuality_EmptyInputs_ReturnsEmptyDisplayText()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(qualityName: "");

        await Assert.That(quality.DisplayText).IsEmpty();
    }

    [Test]
    public async Task FromRadarrQuality_AllFieldsPopulated_DisplayTextContainsAllParts()
    {
        QualityInfo quality = ArrQualityMapper.FromRadarrQuality(
            qualityName: "Remux-2160p",
            resolution: R2160p,
            videoCodec: x265,
            audioCodec: TrueHD,
            hdrType: HdrTypes.HDR10Plus,
            source: QualitySources.Remux);

        await Assert.That(quality.DisplayText).Contains("4K");
        await Assert.That(quality.DisplayText).Contains("Remux");
        await Assert.That(quality.DisplayText).Contains("HDR10+");
        await Assert.That(quality.DisplayText).Contains(x265);
        await Assert.That(quality.DisplayText).Contains(TrueHD);
    }
}

public class ResolutionAliasTests
{
    [Test]
    [Arguments("2160p", "2160p")]
    [Arguments("4K", "2160p")]
    [Arguments("uhd", "2160p")]
    [Arguments("UHD", "2160p")]
    [Arguments("1080p", "1080p")]
    [Arguments("FHD", "1080p")]
    [Arguments("720p", "720p")]
    [Arguments("HD", "720p")]
    [Arguments("480p", "480p")]
    [Arguments("SD", "480p")]
    public async Task Normalize_ResolvesCommonAliases(string input, string expected)
    {
        await Assert.That(Resolutions.Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task Normalize_UsesQualityName_WhenResolutionMissing()
    {
        await Assert.That(Resolutions.Normalize(null, "Bluray-4K")).IsEqualTo(Resolutions.R2160p);
        await Assert.That(Resolutions.Normalize(null, "WEBDL-1080p")).IsEqualTo(Resolutions.R1080p);
    }

    [Test]
    public async Task DisplayName_CanonicalisesTheTier()
    {
        await Assert.That(Resolutions.DisplayName("4k")).IsEqualTo("4K");
        await Assert.That(Resolutions.DisplayName("fhd")).IsEqualTo("1080p");
    }
}
