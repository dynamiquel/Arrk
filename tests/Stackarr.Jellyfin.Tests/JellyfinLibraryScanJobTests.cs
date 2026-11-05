using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core;
using Stackarr.Core.Enums;
using Stackarr.Core.Services;
using Stackarr.Jellyfin.Jobs;
using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

public class JellyfinLibraryScanJobTests
{
    static (JellyfinLibraryScanJob Job, FakeHttpMessageHandler Handler) Create(bool healthy = true)
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("{}"))
        };
        JellyfinClient client = new("http://jellyfin", () => ("admin", "admin"), handler: handler);
        client.SetToken("test-token");

        ServiceRegistry registry = new();
        Service service = new(new JellyfinServiceDefinition());
        service.State.AggregateState = healthy ? ContainerState.Running : ContainerState.Exited;
        registry.Register(service);

        return (new(registry, client, NullLogger<JellyfinLibraryScanJob>.Instance), handler);
    }

    [Test]
    public async Task Execute_HealthyJellyfin_TriggersLibraryRefresh()
    {
        (JellyfinLibraryScanJob job, FakeHttpMessageHandler handler) = Create();

        await job.Execute(CancellationToken.None);

        await Assert.That(handler.Requests.Any(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("Library/Refresh"))).IsTrue();
    }

    [Test]
    public async Task Execute_UnhealthyJellyfin_DoesNotCallJellyfin()
    {
        (JellyfinLibraryScanJob job, FakeHttpMessageHandler handler) = Create(healthy: false);

        await job.Execute(CancellationToken.None);

        await Assert.That(handler.Requests).IsEmpty();
    }
}
