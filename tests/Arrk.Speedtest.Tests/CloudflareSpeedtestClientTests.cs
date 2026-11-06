using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Speedtest.Models;
using Arrk.TestSupport;

namespace Arrk.Speedtest.Tests;

public class CloudflareSpeedtestClientTests
{
    static FakeHttpMessageHandler Handler()
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("ok"));
        return handler;
    }

    static CloudflareSpeedtestClient Client(HttpClient http) =>
        new(http, NullLogger<CloudflareSpeedtestClient>.Instance) { Profile = SpeedtestProfile.Fast };

    [Test]
    public async Task RunTestAsync_WithExplicitClient_UsesProvidedClient()
    {
        // The injected client points at a dead port; the explicit client must be the one used.
        CloudflareSpeedtestClient client = Client(new HttpClient { Timeout = TimeSpan.FromSeconds(2) });

        FakeHttpMessageHandler handler = Handler();
        using HttpClient explicitClient = new(handler) { Timeout = TimeSpan.FromSeconds(30) };

        CloudflareSpeedtestResult result = await client.RunTestAsync(explicitClient);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(handler.Requests.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task RunTestAsync_DefaultClient_UsesInjectedClient()
    {
        FakeHttpMessageHandler handler = Handler();
        CloudflareSpeedtestClient client = Client(new HttpClient(handler));

        CloudflareSpeedtestResult result = await client.RunTestAsync();

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(handler.Requests.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task RunTestAsync_TransientRequestFailure_RetriesInsteadOfFailingTest()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueThrow(new HttpRequestException("proxy tunnel failed with status 503"));
        handler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("ok"));

        CloudflareSpeedtestClient client = Client(new HttpClient(handler));

        CloudflareSpeedtestResult result = await client.RunTestAsync();

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(handler.Requests.Count).IsGreaterThan(6);
    }
}
