using Arrk.Core.Requests;
namespace Arrk.Requests.Tests;

public class ProviderIdMapTests
{
    [Test]
    public async Task Serialize_drops_blank_values()
    {
        string json = ProviderIdMap.Serialize(new Dictionary<string, string>
        {
            ["tmdb"] = "123",
            ["tvdb"] = "",
            ["imdb"] = "tt1"
        });

        IReadOnlyDictionary<string, string> round = ProviderIdMap.Deserialize(json);

        await Assert.That(round["tmdb"]).IsEqualTo("123");
        await Assert.That(round["imdb"]).IsEqualTo("tt1");
        await Assert.That(round.ContainsKey("tvdb")).IsFalse();
    }

    [Test]
    public async Task Deserialize_invalid_json_returns_empty()
    {
        await Assert.That(ProviderIdMap.Deserialize("not json").Count).IsEqualTo(0);
        await Assert.That(ProviderIdMap.Deserialize("").Count).IsEqualTo(0);
        await Assert.That(ProviderIdMap.Deserialize(null).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Overlaps_detects_shared_ids()
    {
        IReadOnlyDictionary<string, string> a = ProviderIdMap.Deserialize("""{"tmdb":"123"}""");
        IReadOnlyDictionary<string, string> b = ProviderIdMap.Deserialize("""{"tmdb":"123","tvdb":"9"}""");
        IReadOnlyDictionary<string, string> c = ProviderIdMap.Deserialize("""{"tmdb":"999"}""");

        await Assert.That(ProviderIdMap.Overlaps(a, b)).IsTrue();
        await Assert.That(ProviderIdMap.Overlaps(a, c)).IsFalse();
    }
}
