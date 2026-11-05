using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

public class RadarrSearchTests
{
    static RadarrService Radarr(FakeHttpMessageHandler handler) =>
        new(new RadarrClient("http://localhost", "key", handler), NullLogger<RadarrService>.Instance);

    [Test]
    public async Task MissingSearch_SendsNativeMissingMoviesSearch()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("{}"))
        };

        await Radarr(handler).SearchMissingAsync();

        CapturedRequest command = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/command");
        await Assert.That(command.Body).Contains("MissingMoviesSearch");
    }
}
