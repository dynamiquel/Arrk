using Arrk.Prowlarr;
using Arrk.TestSupport;

namespace Arrk.Prowlarr.Tests;

/// <summary>
/// The live Prowlarr search response uses JSON types that differ from the older model (numeric
/// <c>indexerId</c>, ISO-8601 <c>publishDate</c>). These lock in that the client parses a real
/// payload and builds a spec-compliant (repeated-category) search URL.
/// </summary>
public class ProwlarrClientSearchTests
{
    const string RealisticPayload = """
    [
      {
        "guid": "magnet:?xt=urn:btih:38BA103ADFBA02CC4580FB6D6A1593CDA9E66EFF",
        "title": "Halo 3 - ODST - OST (Kingdom Music) 2009",
        "size": 201139725,
        "indexerId": 1,
        "indexer": "The Pirate Bay",
        "publishDate": "2009-09-25T17:17:23Z",
        "infoHash": "38BA103ADFBA02CC4580FB6D6A1593CDA9E66EFF",
        "seeders": 2,
        "leechers": 0,
        "protocol": "torrent",
        "categories": [ { "id": 3000, "name": "Audio" } ]
      }
    ]
    """;

    [Test]
    public async Task Search_ParsesRealisticPayload_AndSendsRepeatedCategories()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(RealisticPayload);
        ProwlarrClient client = new("http://prowlarr:9696/prowlarr", "arrk", handler);

        List<ProwlarrSearchResult> results = await client.Search("Halo 3 ODST", categories: new[] { 3000, 3010, 3040 });

        await Assert.That(results).HasCount(1);
        await Assert.That(results[0].IndexerId).IsEqualTo("1");
        await Assert.That(results[0].Seeders).IsEqualTo(2);
        await Assert.That(results[0].Guid).StartsWith("magnet:");
        await Assert.That(results[0].PublishDate).IsNotNull();
        await Assert.That(results[0].PublishDate!.Value.Year).IsEqualTo(2009);

        string query = handler.Requests.Single().Uri.Query;
        await Assert.That(query).Contains("type=search");
        await Assert.That(query).Contains("categories=3000");
        await Assert.That(query).Contains("categories=3010");
        await Assert.That(query).Contains("categories=3040");
        // A comma-separated category list is rejected by Prowlarr (400), so it must not appear.
        await Assert.That(query).DoesNotContain("categories=3000,");
    }

    [Test]
    public async Task Search_ParsesSizesBeyondInt32_WithoutFailingTheWholePayload()
    {
        // Real indexers return sizes above Int32.MaxValue, so the field must parse the full range or
        // the whole response fails.
        const string payload = """
        [
          { "guid": "magnet:?xt=urn:btih:AAA", "title": "Big Collection", "size": 5368709120,
            "indexer": "The Pirate Bay", "protocol": "torrent", "seeders": 1, "categories": [] },
          { "guid": "magnet:?xt=urn:btih:BBB", "title": "Halo 3 OST", "size": 201139725,
            "indexer": "The Pirate Bay", "protocol": "torrent", "seeders": 2, "categories": [] }
        ]
        """;
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(payload);
        ProwlarrClient client = new("http://prowlarr:9696/prowlarr", "arrk", handler);

        List<ProwlarrSearchResult> results = await client.Search("Halo 3", categories: new[] { 3000 });

        await Assert.That(results).HasCount(2);
        await Assert.That(results[0].Size).IsEqualTo(5368709120L);
    }
}
