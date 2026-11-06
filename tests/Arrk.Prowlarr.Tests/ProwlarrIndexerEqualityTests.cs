using Arrk.Prowlarr;

namespace Arrk.Prowlarr.Tests;

/// <summary>
/// Pure logic for <see cref="ProwlarrIndexer.Equals"/>, which drives whether an indexer is
/// considered "unchanged" during sync. No HTTP involved.
/// </summary>
public class ProwlarrIndexerEqualityTests
{
    static ProwlarrIndexer Indexer(
        string name = "TPB",
        string implementation = "Cardigann",
        string configContract = "CardigannSettings",
        string protocol = "torrent",
        bool enable = true,
        int priority = 25,
        int appProfileId = 1,
        bool enableRss = true,
        bool enableAutomaticSearch = true,
        bool enableInteractiveSearch = true,
        int downloadClientId = 0,
        string[]? categories = null,
        params (string Name, object? Value)[] fields) => new()
    {
        Name = name,
        Implementation = implementation,
        ConfigContract = configContract,
        Protocol = protocol,
        Enable = enable,
        Priority = priority,
        AppProfileId = appProfileId,
        EnableRss = enableRss,
        EnableAutomaticSearch = enableAutomaticSearch,
        EnableInteractiveSearch = enableInteractiveSearch,
        DownloadClientId = downloadClientId,
        Categories = categories ?? ["2000"],
        Fields = fields.Select(f => new ProwlarrField { Name = f.Name, Value = f.Value }).ToList()
    };

    [Test]
    public async Task Identical_Indexers_AreEqual()
    {
        ProwlarrIndexer a = Indexer(fields: [("baseUrl", "http://tpb")]);
        ProwlarrIndexer b = Indexer(fields: [("baseUrl", "http://tpb")]);

        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task Name_IsComparedCaseInsensitively()
    {
        await Assert.That(Indexer(name: "TPB").Equals(Indexer(name: "tpb"))).IsTrue();
    }

    [Test]
    public async Task Implementation_IsComparedCaseInsensitively()
    {
        await Assert.That(Indexer(implementation: "CARDIGANN").Equals(Indexer(implementation: "cardigann"))).IsTrue();
    }

    [Test]
    [Arguments(true, false)]
    public async Task Enable_Difference_MakesThemUnequal(bool left, bool right)
    {
        await Assert.That(Indexer(enable: left).Equals(Indexer(enable: right))).IsFalse();
    }

    [Test]
    public async Task Priority_Difference_MakesThemUnequal()
    {
        await Assert.That(Indexer(priority: 25).Equals(Indexer(priority: 50))).IsFalse();
    }

    [Test]
    public async Task AppProfile_Difference_MakesThemUnequal()
    {
        await Assert.That(Indexer(appProfileId: 1).Equals(Indexer(appProfileId: 2))).IsFalse();
    }

    [Test]
    public async Task RssFlags_Difference_MakesThemUnequal()
    {
        await Assert.That(Indexer(enableRss: true).Equals(Indexer(enableRss: false))).IsFalse();
        await Assert.That(Indexer(enableAutomaticSearch: true).Equals(Indexer(enableAutomaticSearch: false))).IsFalse();
        await Assert.That(Indexer(enableInteractiveSearch: true).Equals(Indexer(enableInteractiveSearch: false))).IsFalse();
    }

    [Test]
    public async Task DownloadClient_Difference_MakesThemUnequal()
    {
        await Assert.That(Indexer(downloadClientId: 0).Equals(Indexer(downloadClientId: 7))).IsFalse();
    }

    [Test]
    public async Task Categories_AreComparedCaseInsensitively_AndOrderSensitively()
    {
        await Assert.That(Indexer(categories: ["2000", "5000"]).Equals(Indexer(categories: ["2000", "5000"]))).IsTrue();
        await Assert.That(Indexer(categories: ["2000", "5000"]).Equals(Indexer(categories: ["5000", "2000"]))).IsFalse();
        await Assert.That(Indexer(categories: ["TV"]).Equals(Indexer(categories: ["tv"]))).IsTrue();
        await Assert.That(Indexer(categories: ["2000"]).Equals(Indexer(categories: ["2000", "5000"]))).IsFalse();
    }

    [Test]
    public async Task FieldValue_Difference_MakesThemUnequal()
    {
        await Assert.That(
            Indexer(fields: [("baseUrl", "http://a")]).Equals(Indexer(fields: [("baseUrl", "http://b")])))
            .IsFalse();
    }

    [Test]
    public async Task FieldNames_AreComparedCaseInsensitively()
    {
        await Assert.That(
            Indexer(fields: [("baseUrl", "http://a")]).Equals(Indexer(fields: [("BASEURL", "http://a")])))
            .IsTrue();
    }

    [Test]
    public async Task ExtraField_MakesThemUnequal()
    {
        await Assert.That(
            Indexer(fields: [("baseUrl", "http://a")])
                .Equals(Indexer(fields: [("baseUrl", "http://a"), ("cookie", "x")])))
            .IsFalse();
    }

    [Test]
    public async Task FieldValue_ComparedByStringRepresentation()
    {
        await Assert.That(
            Indexer(fields: [("port", 9091)])
                .Equals(Indexer(fields: [("port", "9091")])))
            .IsTrue();
    }

    [Test]
    public async Task NotAnIndexer_IsNotEqual()
    {
        await Assert.That(Indexer().Equals("not an indexer")).IsFalse();
        await Assert.That(Indexer().Equals(null)).IsFalse();
    }

    [Test]
    public async Task GetHashCode_IsStableForEqualNames()
    {
        await Assert.That(Indexer(name: "TPB").GetHashCode()).IsEqualTo(Indexer(name: "TPB").GetHashCode());
    }
}
