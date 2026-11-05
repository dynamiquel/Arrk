using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core;
using Stackarr.Prowlarr;
using Stackarr.Prowlarr.Configuration;
using Stackarr.TestSupport;

namespace Stackarr.Prowlarr.Tests;

/// <summary>
/// Integration tests against a REAL Prowlarr container. The real <see cref="ProwlarrClient"/>
/// and <see cref="ProwlarrService"/> talk to the real application over its published port.
/// Skipped automatically when Docker is unavailable.
/// </summary>
[NotInParallel(ProwlarrServerShared.ParallelKey)]
public class ProwlarrIntegrationTests
{
    static Task<ProwlarrServer?> GetServerAsync() => ProwlarrServerShared.GetServerAsync();

    // Client: positive

    [Test]
    public async Task GetSystemStatus_WithRealApiKey_ReturnsVersion()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrSystemStatus status = await client.GetSystemStatus();

        await Assert.That(status.Version).IsNotEmpty();
        await Assert.That(status.InstanceName).IsNotEmpty();
    }

    [Test]
    public async Task GetSystemStatus_WithBadApiKey_Throws()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient bad = new(server.Url, "not-a-real-key");

        await Assert.That(async () => await bad.GetSystemStatus()).Throws<HttpRequestException>();
    }

    [Test]
    public async Task GetIndexerDefinitions_ReturnsRealSchema()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        List<ProwlarrIndexerDefinition> definitions = await server.CreateClient().GetIndexerDefinitions();

        await Assert.That(definitions).IsNotEmpty();
        await Assert.That(definitions.Any(d => !string.IsNullOrEmpty(d.DefinitionFile))).IsTrue();
    }

    [Test]
    public async Task GetAppProfiles_ReturnsAtLeastOne()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        List<ProwlarrAppProfile> profiles = await server.CreateClient().GetAppProfiles();

        await Assert.That(profiles).IsNotEmpty();
        await Assert.That(profiles[0].Id).IsGreaterThan(0);
    }

    [Test]
    public async Task GetIndexers_StartsEmpty()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        List<ProwlarrIndexer> indexers = await server.CreateClient().GetIndexers();

        await Assert.That(indexers).IsEmpty();
    }

    [Test]
    public async Task Search_ReturnsResultList()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        List<ProwlarrSearchResult> results = await server.CreateClient().Search("ubuntu");

        await Assert.That(results).IsNotNull();
    }

    [Test]
    public async Task GetIndexerStats_ReturnsStatsObject()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrIndexerStats stats = await server.CreateClient().GetIndexerStats();

        await Assert.That(stats).IsNotNull();
    }

    // Indexer CRUD with a real definition

    static async Task<ProwlarrIndexerDefinition> FirstUsableDefinition(ProwlarrClient client)
    {
        List<ProwlarrIndexerDefinition> definitions = await client.GetIndexerDefinitions();
        // Prefer a well-known public tracker whose schema needs no credentials; fall back to any
        // torrent definition if that one is ever removed from Prowlarr.
        ProwlarrIndexerDefinition? known = definitions.FirstOrDefault(
            d => d.DefinitionFile.Equals("thepiratebay", StringComparison.OrdinalIgnoreCase));
        return known ?? definitions.First(d =>
            !string.IsNullOrEmpty(d.DefinitionFile)
            && !d.Protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task CreateUpdateDeleteIndexer_RoundTrips()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrIndexerDefinition definition = await FirstUsableDefinition(client);

        ProwlarrIndexer payload = new()
        {
            Name = $"it-{Guid.NewGuid():N}"[..18],
            Implementation = definition.Implementation,
            ConfigContract = definition.ConfigContract,
            Protocol = string.IsNullOrEmpty(definition.Protocol) ? "torrent" : definition.Protocol,
            Enable = true,
            Priority = 25,
            AppProfileId = 1,
            Fields =
            [
                new ProwlarrField { Name = "definitionFile", Value = definition.DefinitionFile, Required = true }
            ],
            Categories = ["2000"]
        };

        ProwlarrIndexer created = await client.CreateIndexer(payload);
        try
        {
            await Assert.That(created.Id).IsGreaterThan(0);
            await Assert.That(created.Name).IsEqualTo(payload.Name);

            List<ProwlarrIndexer> indexers = await client.GetIndexers();
            await Assert.That(indexers.Any(i => i.Id == created.Id)).IsTrue();

            created.Priority = 40;
            ProwlarrIndexer updated = await client.UpdateIndexer(created);
            await Assert.That(updated.Priority).IsEqualTo(40);

            List<ProwlarrIndexer> afterUpdate = await client.GetIndexers();
            await Assert.That(afterUpdate.Single(i => i.Id == created.Id).Priority).IsEqualTo(40);
        }
        finally
        {
            await client.DeleteIndexer(created.Id);
        }

        await Assert.That((await client.GetIndexers()).Any(i => i.Id == created.Id)).IsFalse();
    }

    [Test]
    public async Task DeleteIndexer_NonExistent_IsIdempotent()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();

        // Real Prowlarr returns 200 for deleting an id that does not exist.
        await client.DeleteIndexer(999_999);
    }

    // ProwlarrService.SyncIndexers against the real container

    static ProwlarrService CreateService(ProwlarrClient client, ProwlarrConfig config, SolverConfig? solver = null)
    {
        config.Solver = solver ?? new SolverConfig();
        return new ProwlarrService(
            client,
            new MutableOptionsMonitor<ProwlarrConfig>(config),
            new StackarrInstance(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "test" })
                .Build()),
            NullLogger<ProwlarrService>.Instance);
    }

    static IndexerDefinition ConfigEntry(string name, string implementation, string url = "https://example.com") => new()
    {
        Name = name,
        Implementation = implementation,
        Enabled = true,
        Url = url,
        Priority = 20,
        Categories = ["2000"],
        Settings = new Dictionary<string, object>()
    };

    [Test]
    public async Task SyncIndexers_NoEntries_DoesNothing()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrService service = CreateService(client, new ProwlarrConfig());

        await service.SyncIndexers();

        await Assert.That(await client.GetIndexers()).IsEmpty();
    }

    [Test]
    public async Task SyncIndexers_CreatesConfiguredIndexer_AndIsIdempotent()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrIndexerDefinition definition = await FirstUsableDefinition(client);
        ProwlarrConfig config = new();
        config.Entries.Add(ConfigEntry($"sync-{Guid.NewGuid():N}"[..16], definition.DefinitionFile, "https://thepiratebay.org"));
        ProwlarrService service = CreateService(client, config);

        try
        {
            ProwlarrSyncResult first = await service.SyncIndexers();
            await Assert.That(first.Created).IsEqualTo(1);
            await Assert.That(first.Updated).IsEqualTo(0);

            List<ProwlarrIndexer> created = await client.GetIndexers();
            await Assert.That(created.Count).IsEqualTo(1);
            await Assert.That(created[0].Name).IsEqualTo(config.Entries[0].Name);
            await Assert.That(created[0].Enable).IsTrue();

            // Second run must be a pure no-op: real Prowlarr returns the indexer with extra schema
            // fields and omits categories/search flags, which must not look like a change.
            ProwlarrSyncResult second = await service.SyncIndexers();
            await Assert.That(second.Created).IsEqualTo(0);
            await Assert.That(second.Updated).IsEqualTo(0);
            await Assert.That(second.Unchanged).IsEqualTo(1);
            await Assert.That(second.IsNoOp).IsTrue();

            await Assert.That((await client.GetIndexers()).Count).IsEqualTo(1);
        }
        finally
        {
            foreach (ProwlarrIndexer indexer in await client.GetIndexers())
                await client.DeleteIndexer(indexer.Id);
        }
    }

    [Test]
    public async Task SyncIndexers_RepeatedRuns_DoNotChurn()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrIndexerDefinition definition = await FirstUsableDefinition(client);
        ProwlarrConfig config = new();
        config.Entries.Add(ConfigEntry($"stable-{Guid.NewGuid():N}"[..16], definition.DefinitionFile, "https://thepiratebay.org"));
        ProwlarrService service = CreateService(client, config);

        try
        {
            await service.SyncIndexers();
            ProwlarrIndexer afterFirst = (await client.GetIndexers()).Single();

            for (int i = 0; i < 3; i++)
            {
                ProwlarrSyncResult result = await service.SyncIndexers();
                await Assert.That(result.Updated).IsEqualTo(0);
                await Assert.That(result.Created).IsEqualTo(0);
            }

            ProwlarrIndexer afterRepeats = (await client.GetIndexers()).Single();
            await Assert.That(afterRepeats.Id).IsEqualTo(afterFirst.Id);
            await Assert.That(afterRepeats.Added).IsEqualTo(afterFirst.Added);
        }
        finally
        {
            foreach (ProwlarrIndexer indexer in await client.GetIndexers())
                await client.DeleteIndexer(indexer.Id);
        }
    }

    [Test]
    public async Task SyncIndexers_OutOfBandChange_IsCorrected()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrIndexerDefinition definition = await FirstUsableDefinition(client);
        string name = $"drift-{Guid.NewGuid():N}"[..16];

        ProwlarrConfig original = new();
        original.Entries.Add(ConfigEntry(name, definition.DefinitionFile, "https://thepiratebay.org"));

        try
        {
            ProwlarrService service = CreateService(client, original);
            await service.SyncIndexers();
            await Assert.That((await client.GetIndexers()).Single().Priority).IsEqualTo(original.Entries[0].Priority);

            // Introduce drift through a config change, then revert; the revert must be applied.
            // Prowlarr constrains priority to 1..50, so pick a valid but different value.
            ProwlarrConfig drifted = new();
            IndexerDefinition driftedEntry = ConfigEntry(name, definition.DefinitionFile, "https://thepiratebay.org");
            driftedEntry.Priority = 49;
            drifted.Entries.Add(driftedEntry);

            ProwlarrSyncResult update = await CreateService(client, drifted).SyncIndexers();
            await Assert.That(update.Updated).IsEqualTo(1);
            await Assert.That((await client.GetIndexers()).Single().Priority).IsEqualTo(49);

            ProwlarrSyncResult revert = await CreateService(client, original).SyncIndexers();
            await Assert.That(revert.Updated).IsEqualTo(1);
            await Assert.That((await client.GetIndexers()).Single().Priority).IsEqualTo(original.Entries[0].Priority);

            // And once reverted it settles back to a no-op.
            ProwlarrSyncResult settled = await CreateService(client, original).SyncIndexers();
            await Assert.That(settled.IsNoOp).IsTrue();
        }
        finally
        {
            foreach (ProwlarrIndexer indexer in await client.GetIndexers())
                await client.DeleteIndexer(indexer.Id);
        }
    }

    [Test]
    public async Task SyncIndexers_UnknownImplementation_IsSkipped()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrConfig config = new();
        config.Entries.Add(ConfigEntry("missing-impl", "this-does-not-exist-zzz"));
        ProwlarrService service = CreateService(client, config);

        await service.SyncIndexers();

        await Assert.That(await client.GetIndexers()).IsEmpty();
    }

    [Test]
    public async Task SyncIndexers_DisabledEntry_IsNotCreated()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrIndexerDefinition definition = await FirstUsableDefinition(client);
        ProwlarrConfig config = new();
        IndexerDefinition entry = ConfigEntry("disabled-one", definition.DefinitionFile);
        entry.Enabled = false;
        config.Entries.Add(entry);
        ProwlarrService service = CreateService(client, config);

        await service.SyncIndexers();

        await Assert.That(await client.GetIndexers()).IsEmpty();
    }

    [Test]
    public async Task SyncIndexers_RemovesOrphanedIndexers()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrIndexerDefinition definition = await FirstUsableDefinition(client);

        // Create an indexer directly, then sync a config that contains a different indexer: the
        // orphan (not named in the config) should be deleted. (An entirely empty config is
        // intentionally treated as "don't touch anything" and returns early.)
        ProwlarrIndexer orphan = await client.CreateIndexer(new ProwlarrIndexer
        {
            Name = $"orphan-{Guid.NewGuid():N}"[..16],
            Implementation = definition.Implementation,
            ConfigContract = definition.ConfigContract,
            Protocol = "torrent",
            Enable = true,
            AppProfileId = 1,
            Fields = [new ProwlarrField { Name = "definitionFile", Value = definition.DefinitionFile }],
            Categories = ["2000"]
        });

        try
        {
            ProwlarrConfig config = new();
            config.Entries.Add(ConfigEntry($"keep-{Guid.NewGuid():N}"[..16], definition.DefinitionFile, "https://thepiratebay.org"));
            ProwlarrService service = CreateService(client, config);
            await service.SyncIndexers();

            List<ProwlarrIndexer> indexers = await client.GetIndexers();
            await Assert.That(indexers.Any(i => i.Id == orphan.Id)).IsFalse();
            await Assert.That(indexers.Any(i => i.Name == config.Entries[0].Name)).IsTrue();
        }
        finally
        {
            foreach (ProwlarrIndexer indexer in await client.GetIndexers())
                await client.DeleteIndexer(indexer.Id);
        }
    }

    // Cloudflare solver proxy + tags

    static async Task CleanSolverState(ProwlarrClient client)
    {
        // Order matters: an indexer or proxy referencing a tag must go before the tag itself
        // (Prowlarr rejects deleting an in-use tag with 409).
        foreach (ProwlarrIndexer indexer in await client.GetIndexers())
            await client.DeleteIndexer(indexer.Id);
        foreach (ProwlarrIndexerProxy proxy in await client.GetIndexerProxies())
            await client.DeleteIndexerProxy(proxy.Id);
        foreach (ProwlarrTag tag in await client.GetTags())
            await client.DeleteTag(tag.Id);
    }

    static string HostField(ProwlarrIndexerProxy proxy) =>
        proxy.Fields.FirstOrDefault(f => f.Name.Equals("host", StringComparison.OrdinalIgnoreCase))?.Value?.ToString() ?? "";

    [Test]
    public async Task SyncIndexerSolver_CreatesProxy_AndIsIdempotent()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        await CleanSolverState(client);

        try
        {
            ProwlarrService service = CreateService(client, new ProwlarrConfig(),
                new SolverConfig { Enabled = true, Host = server.SolverHost });

            int? tagId = await service.SyncIndexerSolver();
            await Assert.That(tagId).IsNotNull();

            List<ProwlarrIndexerProxy> proxies = await client.GetIndexerProxies();
            ProwlarrIndexerProxy proxy = proxies.Single(
                p => p.Implementation.Equals("FlareSolverr", StringComparison.OrdinalIgnoreCase));

            await Assert.That(proxy.Name).IsEqualTo("Byparr");
            await Assert.That(proxy.ConfigContract).IsEqualTo("FlareSolverrSettings");
            await Assert.That(HostField(proxy).TrimEnd('/')).IsEqualTo(server.SolverHost.TrimEnd('/'));
            await Assert.That(proxy.Tags).Contains(tagId!.Value);

            // Second run must not create a second proxy.
            int? again = await service.SyncIndexerSolver();
            await Assert.That(again).IsEqualTo(tagId);
            await Assert.That((await client.GetIndexerProxies()).Count).IsEqualTo(1);
        }
        finally
        {
            await CleanSolverState(client);
        }
    }

    [Test]
    public async Task SyncIndexerSolver_WhenDisabled_RemovesProxy()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        await CleanSolverState(client);

        try
        {
            await CreateService(client, new ProwlarrConfig(),
                new SolverConfig { Enabled = true, Host = server.SolverHost }).SyncIndexerSolver();
            await Assert.That(await client.GetIndexerProxies()).IsNotEmpty();

            int? tagId = await CreateService(client, new ProwlarrConfig(),
                new SolverConfig { Enabled = false }).SyncIndexerSolver();

            await Assert.That(tagId).IsNull();
            await Assert.That(await client.GetIndexerProxies()).IsEmpty();
        }
        finally
        {
            await CleanSolverState(client);
        }
    }

    [Test]
    public async Task SyncIndexers_AppliesTag_AndIsIdempotent()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        await CleanSolverState(client);

        try
        {
            ProwlarrIndexerDefinition definition = await FirstUsableDefinition(client);
            IndexerDefinition entry = ConfigEntry($"tag-{Guid.NewGuid():N}"[..16], definition.DefinitionFile, "https://thepiratebay.org");
            entry.Tags = ["cf"];

            ProwlarrConfig config = new();
            config.Entries.Add(entry);

            ProwlarrService service = CreateService(client, config,
                new SolverConfig { Enabled = true, Host = server.SolverHost });
            ProwlarrSyncResult first = await service.SyncIndexers();

            await Assert.That(first.Created).IsEqualTo(1);

            ProwlarrIndexer created = (await client.GetIndexers()).Single(i => i.Name == entry.Name);
            ProwlarrTag cf = (await client.GetTags()).Single(t => t.Label == "cf");
            await Assert.That(created.Tags).Contains(cf.Id);

            // The tag is now on the indexer, so a re-run must settle to a no-op.
            ProwlarrSyncResult second = await service.SyncIndexers();
            await Assert.That(second.IsNoOp).IsTrue();
            await Assert.That(second.Unchanged).IsEqualTo(1);
        }
        finally
        {
            await CleanSolverState(client);
        }
    }

    [Test]
    public async Task RealSchema_NativeImplementations_AreAddressableByName()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        List<ProwlarrIndexerDefinition> definitions = await client.GetIndexerDefinitions();

        List<ProwlarrIndexerDefinition> native = definitions
            .Where(d => string.IsNullOrEmpty(d.DefinitionFile)
                        && !string.IsNullOrEmpty(d.Implementation)
                        && d.Protocol.Equals("torrent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        await Assert.That(native).IsNotEmpty();

        Dictionary<string, ProwlarrIndexerDefinition> map = ProwlarrService.BuildDefinitionMap(definitions);
        foreach (ProwlarrIndexerDefinition definition in native)
        {
            await Assert.That(map.ContainsKey(definition.Implementation)).IsTrue();
        }

        // RuTracker is the canonical native, credential-based tracker we target.
        await Assert.That(map.ContainsKey("RuTracker")).IsTrue();
        await Assert.That(map["RuTracker"].ConfigContract).IsEqualTo("RuTrackerSettings");
    }

    [Test]
    public async Task GetIndexerProxies_StartsEmpty()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        await CleanSolverState(client);

        await Assert.That(await client.GetIndexerProxies()).IsEmpty();
        await Assert.That(await client.GetTags()).IsEmpty();
    }

    // Concurrency

    [Test]
    public async Task ConcurrentReads_AllSucceed()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        IEnumerable<Task<ProwlarrSystemStatus>> tasks =
            Enumerable.Range(0, 10).Select(_ => client.GetSystemStatus());

        ProwlarrSystemStatus[] results = await Task.WhenAll(tasks);

        await Assert.That(results.Length).IsEqualTo(10);
        await Assert.That(results.All(r => !string.IsNullOrEmpty(r.Version))).IsTrue();
    }

    [Test]
    public async Task ConcurrentIndexerCreates_AreAllPersisted()
    {
        ProwlarrServer? server = await GetServerAsync();
        if (server is null) return;

        ProwlarrClient client = server.CreateClient();
        ProwlarrIndexerDefinition definition = await FirstUsableDefinition(client);

        string prefix = $"cc-{Guid.NewGuid():N}"[..10];
        async Task<ProwlarrIndexer> Create(int i) => await client.CreateIndexer(new ProwlarrIndexer
        {
            Name = $"{prefix}-{i}",
            Implementation = definition.Implementation,
            ConfigContract = definition.ConfigContract,
            Protocol = "torrent",
            Enable = true,
            AppProfileId = 1,
            Fields = [new ProwlarrField { Name = "definitionFile", Value = definition.DefinitionFile }],
            Categories = ["2000"]
        });

        List<ProwlarrIndexer> created = [];
        try
        {
            created = (await Task.WhenAll(Enumerable.Range(0, 5).Select(Create))).ToList();
            List<ProwlarrIndexer> all = await client.GetIndexers();
            await Assert.That(all.Count(i => i.Name.StartsWith(prefix))).IsEqualTo(5);
        }
        finally
        {
            foreach (ProwlarrIndexer indexer in created)
            {
                try { await client.DeleteIndexer(indexer.Id); } catch { }
            }
        }
    }
}
