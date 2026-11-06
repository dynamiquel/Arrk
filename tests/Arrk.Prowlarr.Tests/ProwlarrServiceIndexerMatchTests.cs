using Arrk.Prowlarr;

namespace Arrk.Prowlarr.Tests;

/// <summary>
/// Covers the indexer comparison. Prowlarr returns an indexer with the full merged schema field list
/// and omits some persisted attributes such as categories and the search flags. The comparison must
/// ignore server-added fields and compare only the desired subset; otherwise the sync updates the
/// indexer on every run.
///
/// The <c>Existing</c> payloads below mirror a real Prowlarr GET response.
/// </summary>
public class ProwlarrServiceIndexerMatchTests
{
    static ProwlarrIndexer Desired() => new()
    {
        Name = "synced",
        Implementation = "Cardigann",
        ConfigContract = "CardigannSettings",
        Protocol = "torrent",
        Enable = true,
        Priority = 20,
        AppProfileId = 1,
        // These are set by BuildIndexerPayload but never echoed by Prowlarr.
        EnableRss = true,
        EnableAutomaticSearch = true,
        EnableInteractiveSearch = true,
        SupportsRss = true,
        SupportsSearch = true,
        Categories = ["2000"],
        DownloadClientId = 0,
        Fields =
        [
            new ProwlarrField { Name = "definitionFile", Value = "thepiratebay" },
            new ProwlarrField { Name = "baseUrl", Value = "https://thepiratebay.org" }
        ]
    };

    /// <summary>The shape Prowlarr actually returns: extra fields, no categories, no search flags.</summary>
    static ProwlarrIndexer ExistingFromServer() => new()
    {
        Id = 1,
        Name = "synced",
        Implementation = "Cardigann",
        ConfigContract = "CardigannSettings",
        Protocol = "torrent",
        Enable = true,
        Priority = 20,
        AppProfileId = 1,
        EnableRss = false,
        EnableAutomaticSearch = false,
        EnableInteractiveSearch = false,
        SupportsRss = true,
        SupportsSearch = true,
        DownloadClientId = 0,
        Categories = [],
        Fields =
        [
            new ProwlarrField { Name = "definitionFile", Value = "thepiratebay" },
            new ProwlarrField { Name = "baseUrl", Value = "https://thepiratebay.org" },
            new ProwlarrField { Name = "baseSettings.queryLimit", Value = null },
            new ProwlarrField { Name = "baseSettings.grabLimit", Value = null },
            new ProwlarrField { Name = "baseSettings.limitsUnit", Value = 0 },
            new ProwlarrField { Name = "torrentBaseSettings.appMinimumSeeders", Value = null },
            new ProwlarrField { Name = "torrentBaseSettings.seedRatio", Value = null },
            new ProwlarrField { Name = "torrentBaseSettings.preferMagnetUrl", Value = false },
            new ProwlarrField { Name = "apiurl", Value = "apibay.org" },
            new ProwlarrField { Name = "top100", Value = 6 },
            new ProwlarrField { Name = "info_top100", Value = "some help text" },
            new ProwlarrField { Name = "uploader", Value = null },
            new ProwlarrField { Name = "info_uploader", Value = "more help text" }
        ]
    };

    [Test]
    public async Task Unchanged_ServerShapedIndexer_IsConsideredMatching()
    {
        await Assert.That(ProwlarrService.IndexerMatchesConfig(ExistingFromServer(), Desired())).IsTrue();
    }

    [Test]
    public async Task ExtraServerFields_AreIgnored()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Fields.Add(new ProwlarrField { Name = "site_extra", Value = "whatever" });

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsTrue();
    }

    [Test]
    public async Task MissingCategoriesOnServer_DoesNotCountAsChanged()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Categories = [];

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsTrue();
    }

    [Test]
    public async Task ServerSearchFlagsDiffering_DoesNotCountAsChanged()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.EnableRss = false;
        existing.EnableAutomaticSearch = false;
        existing.EnableInteractiveSearch = false;

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsTrue();
    }

    [Test]
    public async Task ChangedPriority_IsDetected()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Priority = 99;

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsFalse();
    }

    [Test]
    public async Task ChangedEnabled_IsDetected()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Enable = false;

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsFalse();
    }

    [Test]
    public async Task ChangedAppProfile_IsDetected()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.AppProfileId = 2;

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsFalse();
    }

    [Test]
    public async Task ChangedFieldValue_IsDetected()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Fields.First(f => f.Name == "baseUrl").Value = "https://mirror.example";

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsFalse();
    }

    [Test]
    public async Task MissingDesiredField_IsDetected()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Fields.RemoveAll(f => f.Name == "baseUrl");

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsFalse();
    }

    [Test]
    public async Task FieldNameComparison_IsCaseInsensitive()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Fields.First(f => f.Name == "definitionFile").Value = "ThePirateBay";

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsTrue();
    }

    [Test]
    public async Task DifferentIndexerName_IsDetected()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Name = "renamed";

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsFalse();
    }

    [Test]
    public async Task DifferentConfigContract_IsDetected()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.ConfigContract = "OtherSettings";

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, Desired())).IsFalse();
    }

    [Test]
    public async Task MatchingTags_InAnyOrder_AreConsideredEqual()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Tags = [2, 1];
        ProwlarrIndexer desired = Desired();
        desired.Tags = [1, 2];

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, desired)).IsTrue();
    }

    [Test]
    public async Task DifferentTags_AreDetected()
    {
        ProwlarrIndexer existing = ExistingFromServer();
        existing.Tags = [];
        ProwlarrIndexer desired = Desired();
        desired.Tags = [1];

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, desired)).IsFalse();
    }

    [Test]
    public async Task MaskedSecretField_IsTreatedAsMatching()
    {
        // Prowlarr never echoes a password field; it returns asterisks instead.
        ProwlarrIndexer desired = Desired();
        desired.Fields.Add(new ProwlarrField { Name = "password", Value = "supersecret" });

        ProwlarrIndexer existing = ExistingFromServer();
        existing.Fields.Add(new ProwlarrField { Name = "password", Value = "********" });

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, desired)).IsTrue();
    }

    [Test]
    public async Task RealNonSecretField_IsStillCompared()
    {
        ProwlarrIndexer desired = Desired();
        desired.Fields.Add(new ProwlarrField { Name = "username", Value = "alice" });

        ProwlarrIndexer existing = ExistingFromServer();
        existing.Fields.Add(new ProwlarrField { Name = "username", Value = "bob" });

        await Assert.That(ProwlarrService.IndexerMatchesConfig(existing, desired)).IsFalse();
    }
}

/// <summary>
/// The config <c>implementation</c> may name either a Cardigann definition file (e.g. 1337x) or a
/// native implementation with no definition file (e.g. RuTracker). The definition map must resolve
/// both, without letting the shared "Cardigann" implementation name shadow the file keys.
/// </summary>
public class ProwlarrDefinitionMapTests
{
    static ProwlarrIndexerDefinition Cardigann(string definitionFile) => new()
    {
        Implementation = "Cardigann",
        ConfigContract = "CardigannSettings",
        Fields = [new ProwlarrField { Name = "definitionFile", Value = definitionFile }]
    };

    static ProwlarrIndexerDefinition Native(string implementation) => new()
    {
        Implementation = implementation,
        ConfigContract = $"{implementation}Settings",
        Fields = [new ProwlarrField { Name = "baseUrl", Value = "https://example/" }]
    };

    [Test]
    public async Task ResolvesCardigannDefinitionFile_AndNativeImplementation()
    {
        ProwlarrIndexerDefinition tracker1337x = Cardigann("1337x");
        ProwlarrIndexerDefinition rutracker = Native("RuTracker");

        Dictionary<string, ProwlarrIndexerDefinition> map =
            ProwlarrService.BuildDefinitionMap([tracker1337x, rutracker]);

        await Assert.That(map["1337x"]).IsSameReferenceAs(tracker1337x);
        await Assert.That(map["RuTracker"]).IsSameReferenceAs(rutracker);
    }

    [Test]
    public async Task NativeImplementationName_IsUsableAsConfigIdentifier()
    {
        Dictionary<string, ProwlarrIndexerDefinition> map =
            ProwlarrService.BuildDefinitionMap([Native("RuTracker")]);

        await Assert.That(map.ContainsKey("RuTracker")).IsTrue();
        await Assert.That(map["RuTracker"].ConfigContract).IsEqualTo("RuTrackerSettings");
    }

    [Test]
    public async Task DefinitionFile_IsCaseInsensitive()
    {
        Dictionary<string, ProwlarrIndexerDefinition> map =
            ProwlarrService.BuildDefinitionMap([Cardigann("1337x")]);

        await Assert.That(map.ContainsKey("1337X")).IsTrue();
    }

    [Test]
    public async Task FirstDefinitionWins_ForDuplicateKeys()
    {
        ProwlarrIndexerDefinition first = Cardigann("shared");
        ProwlarrIndexerDefinition second = Cardigann("shared");

        Dictionary<string, ProwlarrIndexerDefinition> map =
            ProwlarrService.BuildDefinitionMap([first, second]);

        await Assert.That(map["shared"]).IsSameReferenceAs(first);
    }
}
