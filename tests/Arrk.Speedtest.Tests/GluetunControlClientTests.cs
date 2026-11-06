using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Services;
using Arrk.TestSupport;

namespace Arrk.Speedtest.Tests;

public class GluetunControlClientTests
{
    static ArrkInstance Instance() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "demo" })
        .Build());

    [Test]
    public async Task BaseAddress_TargetsGluetunControlServerPort()
    {
        ArrkInstance arrk = Instance();
        GluetunControlClient client = new(arrk, NullLogger<GluetunControlClient>.Instance);

        await Assert.That(client.BaseAddress)
            .IsEqualTo($"http://{arrk.GetVpnContainerName()}:{GluetunServiceDefinition.ControlServerPort}");
    }

    [Test]
    public async Task RestartAsync_StopsThenStartsVpn_WithApiKey()
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("{}"));

        GluetunControlClient client = new TestableClient(Instance(), handler);
        await client.RestartAsync();

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/v1/vpn/status");
        await Assert.That(handler.Requests[0].Body).Contains("\"stopped\"");
        await Assert.That(handler.Requests[1].Body).Contains("\"running\"");
        await Assert.That(handler.Requests[0].ApiKey).IsEqualTo(GluetunModule.ControlApiKey);
        await Assert.That(handler.Requests[1].ApiKey).IsEqualTo(GluetunModule.ControlApiKey);
    }

    [Test]
    public async Task RestartAsync_NonSuccessStatus_DoesNotThrow()
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (_, _) => Task.FromResult(
            FakeHttpMessageHandler.JsonResponse("nope", System.Net.HttpStatusCode.InternalServerError));

        GluetunControlClient client = new TestableClient(Instance(), handler);

        await client.RestartAsync();
    }

    sealed class TestableClient(ArrkInstance instance, FakeHttpMessageHandler handler)
        : GluetunControlClient(instance, NullLogger<GluetunControlClient>.Instance)
    {
        public override HttpMessageHandler CreateHandler() => handler;
    }
}
