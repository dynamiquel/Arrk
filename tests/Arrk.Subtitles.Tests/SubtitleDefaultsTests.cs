namespace Arrk.Subtitles.Tests;

public class SubtitleDefaultsTests
{
    [Test]
    public async Task Normalize_KeepsConfiguredOrderAndDeduplicates()
    {
        IReadOnlyList<string> normalized = SubtitleLanguages.Normalize(["fr", "EN", "", "de", "fr"]);

        await Assert.That(normalized).IsEquivalentTo(["fr", "en", "de"]);
        await Assert.That(normalized[0]).IsEqualTo("fr");
    }

    [Test]
    public async Task Normalize_DoesNotAddEnglishWhenConfigurationOmitsIt()
    {
        IReadOnlyList<string> normalized = SubtitleLanguages.Normalize(["de"]);

        await Assert.That(normalized).IsEquivalentTo(["de"]);
        await Assert.That(normalized).DoesNotContain("en");
    }

    [Test]
    public async Task Normalize_HandlesNullAndBlankConfiguration()
    {
        IReadOnlyList<string> fromNull = SubtitleLanguages.Normalize(null);
        IReadOnlyList<string> fromBlank = SubtitleLanguages.Normalize([" ", ""]);

        await Assert.That(fromNull).IsEmpty();
        await Assert.That(fromBlank).IsEmpty();
    }

    [Test]
    public async Task NormalizeCode_AcceptsRegionsAndThreeLetterCodes()
    {
        await Assert.That(SubtitleLanguages.NormalizeCode("pt-BR")).IsEqualTo("pt");
        await Assert.That(SubtitleLanguages.NormalizeCode("ENG")).IsEqualTo("eng");
        await Assert.That(SubtitleLanguages.NormalizeCode("")).IsEqualTo("");
    }

    [Test]
    public async Task ToTwoLetter_ConvertsIso639ThreeLetterCodes()
    {
        await Assert.That(SubtitleLanguages.ToTwoLetter("eng")).IsEqualTo("en");
        await Assert.That(SubtitleLanguages.ToTwoLetter("fra")).IsEqualTo("fr");
        await Assert.That(SubtitleLanguages.ToTwoLetter("EN")).IsEqualTo("en");
        await Assert.That(SubtitleLanguages.ToTwoLetter("")).IsEqualTo("");
    }

    [Test]
    public async Task ToTwoLetter_ConvertsBibliographicIso639TwoCodes()
    {
        await Assert.That(SubtitleLanguages.ToTwoLetter("fre")).IsEqualTo("fr");
        await Assert.That(SubtitleLanguages.ToTwoLetter("ger")).IsEqualTo("de");
        await Assert.That(SubtitleLanguages.ToTwoLetter("dut")).IsEqualTo("nl");
        await Assert.That(SubtitleLanguages.ToTwoLetter("chi")).IsEqualTo("zh");
        await Assert.That(SubtitleLanguages.ToTwoLetter("cze")).IsEqualTo("cs");
        await Assert.That(SubtitleLanguages.ToTwoLetter("rum")).IsEqualTo("ro");
    }

    [Test]
    public async Task Matches_ComparesBibliographicAndTwoLetterCodes()
    {
        await Assert.That(SubtitleLanguages.Matches("fre", "fr")).IsTrue();
        await Assert.That(SubtitleLanguages.Matches("ger", "de")).IsTrue();
        await Assert.That(SubtitleLanguages.Matches("eng", "fr")).IsFalse();
    }

    [Test]
    public async Task NormalizeTwoLetter_ConvertsAndDeduplicates()
    {
        IReadOnlyList<string> normalized = SubtitleLanguages.NormalizeTwoLetter(["eng", "EN", "fra", "fr"]);

        await Assert.That(normalized).IsEquivalentTo(["en", "fr"]);
    }

    [Test]
    public async Task DisplayName_ResolvesIsoCodes()
    {
        await Assert.That(SubtitleLanguages.DisplayName("fr")).IsEqualTo("French");
        await Assert.That(SubtitleLanguages.DisplayName("de")).IsEqualTo("German");
        await Assert.That(SubtitleLanguages.DisplayName("fre")).IsEqualTo("French");
        await Assert.That(SubtitleLanguages.DisplayName("")).IsEqualTo("");
    }

}
