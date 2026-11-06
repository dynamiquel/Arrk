using System.Text.RegularExpressions;

namespace Arrk.Tvarr.Tests;

public class CustomFormatDefinitionsTests
{
    static bool Matches(string pattern, string title) =>
        Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase);

    [Test]
    public async Task EveryFormat_HasAtLeastOneSpecificationWithPattern()
    {
        foreach (CustomFormatSpec spec in CustomFormatDefinitions.VideoFormats.Concat(CustomFormatDefinitions.AudioFormats))
        {
            await Assert.That(spec.Specifications).IsNotEmpty();
            await Assert.That(spec.Specifications.All(s => !string.IsNullOrWhiteSpace(s.Pattern))).IsTrue();
        }
    }

    [Test]
    [Arguments("Dolby Vision", "Movie.2021.DoVi.2160p.mkv", true)]
    [Arguments("Dolby Vision", "Movie.2021.DVDRip.mkv", true)]
    [Arguments("Dolby Vision", "Movie.2021.1080p.mkv", false)]
    [Arguments("HDR10+", "Movie.2021.HDR10+.mkv", true)]
    [Arguments("HDR10+", "Movie.2021.HDR.mkv", false)]
    [Arguments("HDR10", "Movie.2021.HDR10.mkv", true)]
    [Arguments("HLG", "Movie.2021.HLG.mkv", true)]
    public async Task VideoPatterns_MatchAsExpected(string format, string title, bool expected)
    {
        CustomFormatSpec spec = CustomFormatDefinitions.VideoFormats.Single(f => f.Name == format);

        bool any = spec.Specifications.Any(s => Matches(s.Pattern, title));
        await Assert.That(any).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Dolby TrueHD", "Movie.2021.TrueHD.7.1.mkv", true)]
    [Arguments("Dolby TrueHD", "Movie.2021.TrueHD.Atmos.mkv", false)]
    [Arguments("Dolby Atmos", "Movie.2021.ATMOS.mkv", true)]
    [Arguments("Dolby Atmos", "Movie.2021.ATMOS7.mkv", true)]
    [Arguments("DTS-X", "Movie.2021.DTS-X.mkv", true)]
    [Arguments("DTS-HD MA", "Movie.2021.DTS-HD.MA.mkv", true)]
    public async Task AudioPatterns_MatchAsExpected(string format, string title, bool expected)
    {
        CustomFormatSpec spec = CustomFormatDefinitions.AudioFormats.Single(f => f.Name == format);

        bool any = spec.Specifications.Any(s => Matches(s.Pattern, title));
        await Assert.That(any).IsEqualTo(expected);
    }

    [Test]
    public async Task Hdr10Pattern_DoesNotMatchHdr10Plus()
    {
        CustomFormatSpec hdr10 = CustomFormatDefinitions.VideoFormats.Single(f => f.Name == "HDR10");

        await Assert.That(hdr10.Specifications.Any(s => Matches(s.Pattern, "Movie.HDR10+.mkv"))).IsFalse();
    }
}
