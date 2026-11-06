using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Mal.Clients;
using Arrk.TestSupport;

namespace Arrk.Mal.Tests;

public class MalClientTests
{
    [Test]
    public async Task Search_UsesTheClientIdHeaderWithoutAUserToken()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler()
            .EnqueueJson("""{"data":[{"node":{"id":52991,"title":"Sousou no Frieren","num_episodes":28,"media_type":"tv"}}]}""");

        MalClient client = new(
            new MutableOptionsMonitor<MalConfig>(new MalConfig { ClientId = "client-id", ClientSecret = "secret" }),
            NullLogger<MalClient>.Instance,
            handler);

        List<MalAnime> results = await client.SearchAnimeAsync("Frieren", null);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Id).IsEqualTo(52991);
        CapturedRequest request = handler.Requests[0];
        await Assert.That(request.Headers).IsNotNull();
        await Assert.That(request.Headers!.TryGetValue("X-MAL-CLIENT-ID", out string? clientId)).IsTrue();
        await Assert.That(clientId).IsEqualTo("client-id");
    }
}
