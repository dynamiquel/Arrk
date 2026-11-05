using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Models;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.IntegrationTests;

/// <summary>
/// Real-container coverage for the Radarr/Sonarr service layer. These exercise the behaviours that
/// only make sense against a live arr (empty-state mapping, quality-definition reads, custom format
/// creation, read-only endpoints, no-op updates, bad-key handling and concurrent reads). The write
/// path for quality profiles is covered by <see cref="CrossModuleIntegrationTests"/> (which mutates
/// and then removes profiles), so this class deliberately does not assert on the default profiles.
/// Skipped when Docker is unavailable.
///
/// Note: creating movies/series is intentionally not covered here because it requires live TMDB/TVDB
/// metadata lookups.
/// </summary>
[NotInParallel(ArrStackShared.ParallelKey)]
public class TvarrServiceIntegrationTests
{
    static Task<ArrStackFixture?> FixtureAsync() => ArrStackShared.GetAsync();

    static RadarrService RadarrService(ArrStackFixture f, out RadarrClient client)
    {
        client = new RadarrClient(f.RadarrUrl, f.RadarrApiKey);
        return new RadarrService(client, NullLogger<RadarrService>.Instance);
    }

    static SonarrService SonarrService(ArrStackFixture f, out SonarrClient client)
    {
        client = new SonarrClient(f.SonarrUrl, f.SonarrApiKey);
        return new SonarrService(client, NullLogger<SonarrService>.Instance);
    }

    // Empty-state mapping

    [Test]
    public async Task Radarr_GetMovie_NonExistent_ReturnsNull()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        RadarrService service = RadarrService(f, out _);

        await Assert.That(await service.GetMovie(999_999)).IsNull();
    }

    [Test]
    public async Task Sonarr_GetSeries_NonExistent_ReturnsNull()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        SonarrService service = SonarrService(f, out _);

        await Assert.That(await service.GetSeries(999_999)).IsNull();
    }

    [Test]
    public async Task Sonarr_GetSeasons_NonExistent_ReturnsEmpty()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        SonarrService service = SonarrService(f, out _);

        await Assert.That(await service.GetSeasons(999_999)).IsEmpty();
    }

    // Quality profile reads

    [Test]
    public async Task Radarr_GetQualityProfiles_ReturnsList()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        RadarrClient client = new(f.RadarrUrl, f.RadarrApiKey);

        // The list may be empty depending on ordering with the quality-profile sync test, but the
        // endpoint must be reachable and return a materialised list.
        List<QualityProfile> profiles = await client.GetQualityProfiles();
        await Assert.That(profiles).IsNotNull();
    }

    [Test]
    public async Task Sonarr_GetQualityProfiles_ReturnsList()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        SonarrClient client = new(f.SonarrUrl, f.SonarrApiKey);

        List<QualityProfile> profiles = await client.GetQualityProfiles();
        await Assert.That(profiles).IsNotNull();
    }

    [Test]
    public async Task Radarr_GetQualityDefinitions_ReturnsDefinitions()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        RadarrClient client = new(f.RadarrUrl, f.RadarrApiKey);

        List<QualityDefinitionDto> definitions = await client.GetQualityDefinitions();
        await Assert.That(definitions).IsNotEmpty();
    }

    // Custom format round-trips

    [Test]
    public async Task Radarr_CustomFormat_Create_RoundTrips()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        RadarrClient client = new(f.RadarrUrl, f.RadarrApiKey);
        string name = $"cf-{Guid.NewGuid():N}"[..16];

        CustomFormatDto created = await client.CreateCustomFormat(new
        {
            name,
            includeCustomFormatWhenRenaming = false,
            specifications = new[]
            {
                new
                {
                    name = "ReleaseTitleSpecification",
                    implementation = "ReleaseTitleSpecification",
                    negate = false,
                    required = true,
                    fields = new[] { new { name = "value", value = "TESTPATTERN" } }
                }
            }
        });

        await Assert.That(created.Id).IsGreaterThan(0);
        await Assert.That((await client.GetCustomFormats()).Any(cf => cf.Name == name)).IsTrue();
    }

    // Read-only endpoints

    [Test]
    public async Task Radarr_GetQueue_And_GetTags_ReturnLists()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        RadarrClient client = new(f.RadarrUrl, f.RadarrApiKey);

        await Assert.That(await client.GetQueue()).IsNotNull();
        await Assert.That(await client.GetTags()).IsNotNull();
    }

    [Test]
    public async Task Sonarr_GetQueue_Tags_And_Calendar_ReturnLists()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        SonarrClient client = new(f.SonarrUrl, f.SonarrApiKey);

        await Assert.That(await client.GetQueue()).IsNotNull();
        await Assert.That(await client.GetTags()).IsNotNull();
        await Assert.That(await client.GetCalendar(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow.AddDays(7))).IsNotNull();
    }

    // No-op updates

    [Test]
    public async Task Sonarr_UpdateSeriesMonitoring_UnknownTvdbId_DoesNothing()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        SonarrService service = SonarrService(f, out SonarrClient client);
        int before = (await client.GetSeries()).Count;

        await service.UpdateSeriesMonitoring(999_999_999, qualityProfileId: 1, monitoredSeasons: [1]);

        await Assert.That((await client.GetSeries()).Count).IsEqualTo(before);
    }

    // Negative

    [Test]
    public async Task Radarr_BadApiKey_ThrowsUnauthorized()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        RadarrClient bad = new(f.RadarrUrl, "definitely-not-the-key");

        await Assert.That(async () => await bad.GetMovies()).Throws<HttpRequestException>();
    }

    [Test]
    public async Task Sonarr_BadApiKey_ThrowsUnauthorized()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        SonarrClient bad = new(f.SonarrUrl, "definitely-not-the-key");

        await Assert.That(async () => await bad.GetSeries()).Throws<HttpRequestException>();
    }

    // Concurrency

    [Test]
    public async Task Radarr_ConcurrentReads_AllSucceed()
    {
        ArrStackFixture? f = await FixtureAsync();
        if (f is null) return;

        RadarrClient client = new(f.RadarrUrl, f.RadarrApiKey);
        IEnumerable<Task<List<QualityProfile>>> tasks = Enumerable.Range(0, 10).Select(_ => client.GetQualityProfiles());

        List<QualityProfile>[] results = await Task.WhenAll(tasks);

        await Assert.That(results.Length).IsEqualTo(10);
        await Assert.That(results.All(r => r is not null)).IsTrue();
    }
}
