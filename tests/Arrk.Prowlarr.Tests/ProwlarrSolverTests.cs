using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Prowlarr.Configuration;
using Arrk.TestSupport;

namespace Arrk.Prowlarr.Tests;

/// <summary>
/// Prowlarr model of Cloudflare solving: a single FlareSolverr-typed indexer proxy pointing at the
/// Byparr container, linked to indexers by a shared tag. These use a fake transport so the exact
/// request sequence and payloads are asserted without a live Prowlarr.
/// </summary>
public class ProwlarrSolverTests
{
    static ProwlarrService Service(FakeHttpMessageHandler handler, SolverConfig? solver = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "test" })
            .Build();

        ProwlarrConfig prowlarrConfig = new() { Solver = solver ?? new SolverConfig { Enabled = true } };

        return new ProwlarrService(
            new ProwlarrClient("http://prowlarr:9696/prowlarr", "arrk", handler),
            new MutableOptionsMonitor<ProwlarrConfig>(prowlarrConfig),
            new ArrkInstance(configuration),
            NullLogger<ProwlarrService>.Instance);
    }

    const string ExistingProxy = """
    [
      {
        "id": 7,
        "name": "Byparr",
        "implementation": "FlareSolverr",
        "configContract": "FlareSolverrSettings",
        "fields": [
          { "name": "host", "value": "http://stackarr-test-byparr:8191/" },
          { "name": "requestTimeout", "value": 180 }
        ],
        "tags": [ 3 ]
      }
    ]
    """;

    const string ExistingTag = """[ { "id": 3, "label": "cf" } ]""";

    [Test]
    public async Task SyncIndexerSolver_CreatesProxyAndTag_WhenNoneExist()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("[]");                                        // GET indexerproxy
        handler.EnqueueJson("[]");                                        // GET tag
        handler.EnqueueJson("""{ "id": 3, "label": "cf" }""");            // POST tag
        handler.EnqueueJson("""{ "id": 7, "name": "Byparr" }""");         // POST indexerproxy

        int? tagId = await Service(handler).SyncIndexerSolver();

        await Assert.That(tagId).IsEqualTo(3);

        CapturedRequest tagPost = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/tag"));
        await Assert.That(tagPost.Body).Contains("\"label\":\"cf\"");

        CapturedRequest proxyPost = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/indexerproxy"));
        await Assert.That(proxyPost.Body).Contains("\"implementation\":\"FlareSolverr\"");
        await Assert.That(proxyPost.Body).Contains("\"configContract\":\"FlareSolverrSettings\"");
        await Assert.That(proxyPost.Body).Contains("http://stackarr-test-byparr:8191/");
        await Assert.That(proxyPost.Body).Contains("\"requestTimeout\"");
        await Assert.That(proxyPost.Body).Contains("\"tags\":[3]");
    }

    [Test]
    public async Task SyncIndexerSolver_UpToDate_IsNoOp()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(ExistingProxy);
        handler.EnqueueJson(ExistingTag);

        int? tagId = await Service(handler).SyncIndexerSolver();

        await Assert.That(tagId).IsEqualTo(3);
        await Assert.That(handler.Requests).HasCount(2);
        await Assert.That(handler.Requests.All(r => r.Method == HttpMethod.Get)).IsTrue();
    }

    [Test]
    public async Task SyncIndexerSolver_UpdatesProxy_WhenHostChanges()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(ExistingProxy);
        handler.EnqueueJson(ExistingTag);
        handler.EnqueueJson("""{ "id": 7, "name": "Byparr" }""");     // PUT indexerproxy

        int? tagId = await Service(handler, new SolverConfig
        {
            Enabled = true,
            Host = "http://solver.internal:8191/"
        }).SyncIndexerSolver();

        await Assert.That(tagId).IsEqualTo(3);

        CapturedRequest put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        await Assert.That(put.Uri.AbsolutePath.EndsWith("/indexerproxy/7")).IsTrue();
        await Assert.That(put.Body).Contains("http://solver.internal:8191/");
    }

    [Test]
    public async Task SyncIndexerSolver_UsesConfiguredTimeout()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("[]");
        handler.EnqueueJson(ExistingTag);
        handler.EnqueueJson("""{ "id": 7, "name": "Byparr" }""");

        await Service(handler, new SolverConfig { Enabled = true, RequestTimeoutSeconds = 240 }).SyncIndexerSolver();

        CapturedRequest post = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/indexerproxy"));
        await Assert.That(post.Body).Contains("\"value\":240");
    }

    [Test]
    public async Task SyncIndexerSolver_UsesCustomTag()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("[]");
        handler.EnqueueJson("[]");
        handler.EnqueueJson("""{ "id": 9, "label": "cloudflare" }""");
        handler.EnqueueJson("""{ "id": 7, "name": "Byparr" }""");

        int? tagId = await Service(handler, new SolverConfig { Enabled = true, Tag = "cloudflare" }).SyncIndexerSolver();

        await Assert.That(tagId).IsEqualTo(9);
        CapturedRequest tagPost = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/tag"));
        await Assert.That(tagPost.Body).Contains("\"label\":\"cloudflare\"");
        CapturedRequest proxyPost = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/indexerproxy"));
        await Assert.That(proxyPost.Body).Contains("\"tags\":[9]");
    }

    [Test]
    public async Task SyncIndexerSolver_WhenDisabled_RemovesProxy()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(ExistingProxy);
        handler.EnqueueJson("");

        int? tagId = await Service(handler, new SolverConfig { Enabled = false }).SyncIndexerSolver();

        await Assert.That(tagId).IsNull();
        CapturedRequest delete = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        await Assert.That(delete.Uri.AbsolutePath.EndsWith("/indexerproxy/7")).IsTrue();
    }

    [Test]
    public async Task SyncIndexerSolver_WhenDisabledAndNoneExist_DoesNothing()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("[]");

        int? tagId = await Service(handler, new SolverConfig { Enabled = false }).SyncIndexerSolver();

        await Assert.That(tagId).IsNull();
        await Assert.That(handler.Requests).HasCount(1);
    }

    [Test]
    public async Task SyncIndexerSolver_RemovesDuplicateProxies()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""
        [
          { "id": 7, "name": "Byparr", "implementation": "FlareSolverr", "configContract": "FlareSolverrSettings",
            "fields": [ { "name": "host", "value": "http://stackarr-test-byparr:8191/" },
                        { "name": "requestTimeout", "value": 180 } ], "tags": [ 3 ] },
          { "id": 8, "name": "Byparr (old)", "implementation": "FlareSolverr", "configContract": "FlareSolverrSettings",
            "fields": [], "tags": [ 3 ] }
        ]
        """);
        handler.EnqueueJson(ExistingTag);
        handler.EnqueueJson("");

        int? tagId = await Service(handler).SyncIndexerSolver();

        await Assert.That(tagId).IsEqualTo(3);
        CapturedRequest delete = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        await Assert.That(delete.Uri.AbsolutePath.EndsWith("/indexerproxy/8")).IsTrue();
    }
}
