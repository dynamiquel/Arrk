using Stackarr.Mal.Services;

namespace Stackarr.Mal.Tests;

public class MalMappingResultTests
{
    [Test]
    public async Task SplitSeason_MapsEpisodesToTheCorrectMalEntry()
    {
        MalMappingResult mapping = new(1, 35760,
        [
            new MalMappedPart(35760, 1, 12, 1),
            new MalMappedPart(38524, 13, 22, 1)
        ]);

        await Assert.That(mapping.TryMapEpisode(1, out int firstId, out int firstEpisode)).IsTrue();
        await Assert.That(firstId).IsEqualTo(35760);
        await Assert.That(firstEpisode).IsEqualTo(1);

        await Assert.That(mapping.TryMapEpisode(13, out int secondId, out int secondEpisode)).IsTrue();
        await Assert.That(secondId).IsEqualTo(38524);
        await Assert.That(secondEpisode).IsEqualTo(1);

        await Assert.That(mapping.TryMapEpisode(22, out int lastId, out int lastEpisode)).IsTrue();
        await Assert.That(lastId).IsEqualTo(38524);
        await Assert.That(lastEpisode).IsEqualTo(10);
    }

    [Test]
    public async Task SeriesWideSeason_UsesTheSeasonOffset()
    {
        MalMappingResult mapping = new(2, 21,
        [
            new MalMappedPart(21, 1, 194, 893)
        ]);

        await Assert.That(mapping.TryMapEpisode(1, out _, out int first)).IsTrue();
        await Assert.That(first).IsEqualTo(893);

        await Assert.That(mapping.TryMapEpisode(10, out _, out int tenth)).IsTrue();
        await Assert.That(tenth).IsEqualTo(902);
    }

    [Test]
    public async Task EpisodeOutsideEveryPart_IsNotMapped()
    {
        MalMappingResult mapping = new(3, 1, [new MalMappedPart(1, 1, 12, 1)]);
        await Assert.That(mapping.TryMapEpisode(13, out _, out _)).IsFalse();
    }
}
