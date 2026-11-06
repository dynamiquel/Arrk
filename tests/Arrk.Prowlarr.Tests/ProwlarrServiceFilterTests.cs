using Arrk.Prowlarr;
using Arrk.Prowlarr.Configuration;

namespace Arrk.Prowlarr.Tests;

public class ProwlarrServiceFilterTests
{
    static ProwlarrConfig Config(params (string Name, bool Enabled)[] entries)
    {
        ProwlarrConfig config = new();
        foreach ((string name, bool enabled) in entries)
        {
            config.Entries.Add(new IndexerDefinition { Name = name, Enabled = enabled });
        }
        return config;
    }

    [Test]
    public async Task NoAllowOrBlock_ReturnsAllEnabled()
    {
        ProwlarrConfig config = Config(("A", true), ("B", true), ("C", false));

        List<string> names = ProwlarrService.GetFilteredIndexerNames(config, new IndexerAccessConfig());

        await Assert.That(names).IsEquivalentTo(["A", "B"]);
    }

    [Test]
    public async Task Allowlist_ReturnsOnlyAllowedEnabledNames()
    {
        ProwlarrConfig config = Config(("A", true), ("B", true), ("C", true));
        IndexerAccessConfig access = new() { Allowlist = ["a", "C"] };

        List<string> names = ProwlarrService.GetFilteredIndexerNames(config, access);

        await Assert.That(names).IsEquivalentTo(["A", "C"]);
    }

    [Test]
    public async Task Allowlist_IgnoresDisabledEntries()
    {
        ProwlarrConfig config = Config(("A", false), ("B", true));
        IndexerAccessConfig access = new() { Allowlist = ["A", "B"] };

        List<string> names = ProwlarrService.GetFilteredIndexerNames(config, access);

        await Assert.That(names).IsEquivalentTo(["B"]);
    }

    [Test]
    public async Task Allowlist_TakesPrecedenceOverBlocklist()
    {
        ProwlarrConfig config = Config(("A", true), ("B", true), ("C", true));
        IndexerAccessConfig access = new() { Allowlist = ["A", "B"], Blocklist = ["A"] };

        List<string> names = ProwlarrService.GetFilteredIndexerNames(config, access);

        // Allowlist wins; A is still included because allowlist is non-empty.
        await Assert.That(names).IsEquivalentTo(["A", "B"]);
    }

    [Test]
    public async Task Blocklist_RemovesBlockedEnabledNames()
    {
        ProwlarrConfig config = Config(("A", true), ("B", true), ("C", true));
        IndexerAccessConfig access = new() { Blocklist = ["b"] };

        List<string> names = ProwlarrService.GetFilteredIndexerNames(config, access);

        await Assert.That(names).IsEquivalentTo(["A", "C"]);
    }

    [Test]
    public async Task EmptyAllowAndBlock_ReturnsAllEnabled()
    {
        ProwlarrConfig config = Config(("A", true));
        IndexerAccessConfig access = new() { Allowlist = [], Blocklist = [] };

        await Assert.That(ProwlarrService.GetFilteredIndexerNames(config, access)).IsEquivalentTo(["A"]);
    }

    [Test]
    public async Task NoEntries_ReturnsEmpty()
    {
        await Assert.That(ProwlarrService.GetFilteredIndexerNames(new ProwlarrConfig(), new IndexerAccessConfig())).IsEmpty();
    }
}
