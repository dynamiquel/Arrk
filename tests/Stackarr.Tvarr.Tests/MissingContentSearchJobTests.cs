using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Enums;
using Stackarr.Core.Services;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Jobs;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// The hourly sweep retries missing content when no deletion event has fired. It must only run for
/// a healthy TV service and must not let one unreachable arr block the other.
/// </summary>
public class MissingContentSearchJobTests
{
    static FakeHttpMessageHandler SonarrHandler() => new()
    {
        Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            request.RequestUri!.AbsolutePath switch
            {
                "/api/v3/wanted/missing" => """
                    {
                      "page": 1,
                      "pageSize": 1000,
                      "totalRecords": 1,
                      "records": [ { "id": 2, "seriesId": 1, "monitored": true, "hasFile": false, "airDateUtc": "2026-01-08T00:00:00Z" } ]
                    }
                    """,
                "/api/v3/queue" => """{ "records": [] }""",
                _ => "{}"
            }))
    };

    static FakeHttpMessageHandler RadarrHandler() => new()
    {
        Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            request.RequestUri!.AbsolutePath switch
            {
                "/api/v3/wanted/missing" => """
                    {
                      "page": 1,
                      "pageSize": 1000,
                      "totalRecords": 1,
                      "records": [ { "id": 7, "title": "Missing", "monitored": true, "hasFile": false } ]
                    }
                    """,
                "/api/v3/queue" => """{ "records": [] }""",
                _ => "{}"
            }))
    };

    static MissingContentSearchJob Job(
        ServiceRegistry registry,
        FakeHttpMessageHandler sonarrHandler,
        FakeHttpMessageHandler radarrHandler,
        TvConfig? config = null)
    {
        SonarrService sonarr = new(
            new SonarrClient("http://localhost", "key", sonarrHandler),
            NullLogger<SonarrService>.Instance);
        RadarrService radarr = new(
            new RadarrClient("http://localhost", "key", radarrHandler),
            NullLogger<RadarrService>.Instance);

        return new MissingContentSearchJob(
            registry,
            sonarr,
            radarr,
            new MutableOptionsMonitor<TvConfig>(config ?? new TvConfig()),
            NullLogger<MissingContentSearchJob>.Instance);
    }

    static ServiceRegistry HealthyTvRegistry()
    {
        ServiceRegistry registry = new();
        Service tv = new(new TvServiceDefinition())
        {
            State = new ServiceState
            {
                ServiceKey = TvServiceDefinition.StaticKey,
                AggregateState = ContainerState.Running
            }
        };
        registry.Register(tv);
        return registry;
    }

    [Test]
    public async Task SearchesMissingEpisodesAndMovies()
    {
        FakeHttpMessageHandler sonarr = SonarrHandler();
        FakeHttpMessageHandler radarr = RadarrHandler();
        MissingContentSearchJob job = Job(HealthyTvRegistry(), sonarr, radarr);

        await job.Execute(CancellationToken.None);

        await Assert.That(sonarr.Requests.Any(r =>
            r.Method == HttpMethod.Post && r.Body.Contains("MissingEpisodeSearch"))).IsTrue();
        await Assert.That(radarr.Requests.Any(r =>
            r.Method == HttpMethod.Post && r.Body.Contains("MissingMoviesSearch"))).IsTrue();
    }

    [Test]
    public async Task Disabled_DoesNotContactEitherArr()
    {
        FakeHttpMessageHandler sonarr = SonarrHandler();
        FakeHttpMessageHandler radarr = RadarrHandler();
        MissingContentSearchJob job = Job(
            HealthyTvRegistry(), sonarr, radarr,
            new TvConfig { MissingSearch = new TvMissingSearchConfig { Enabled = false } });

        await job.Execute(CancellationToken.None);

        await Assert.That(sonarr.Requests).IsEmpty();
        await Assert.That(radarr.Requests).IsEmpty();
    }

    [Test]
    public async Task UnhealthyTvService_DoesNotContactEitherArr()
    {
        FakeHttpMessageHandler sonarr = SonarrHandler();
        FakeHttpMessageHandler radarr = RadarrHandler();
        MissingContentSearchJob job = Job(new ServiceRegistry(), sonarr, radarr);

        await job.Execute(CancellationToken.None);

        await Assert.That(sonarr.Requests).IsEmpty();
        await Assert.That(radarr.Requests).IsEmpty();
    }

    [Test]
    public async Task OneArrFailure_DoesNotBlockTheOther()
    {
        FakeHttpMessageHandler sonarr = new()
        {
            Fallback = (_, _) => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Sonarr is down"))
        };
        FakeHttpMessageHandler radarr = RadarrHandler();
        MissingContentSearchJob job = Job(HealthyTvRegistry(), sonarr, radarr);

        await job.Execute(CancellationToken.None);

        await Assert.That(radarr.Requests.Any(r =>
            r.Method == HttpMethod.Post && r.Body.Contains("MissingMoviesSearch"))).IsTrue();
    }
}
