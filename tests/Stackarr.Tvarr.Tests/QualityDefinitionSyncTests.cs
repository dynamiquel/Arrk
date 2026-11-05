using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Configuration;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// Radarr and Sonarr default every quality definition to <c>minSize = 0</c>, which lets a 3 GB
/// "4K" release through. Stackarr applies an Mbps floor per resolution tier so a 4K grab is
/// meaningfully larger than a good 1080p one. Size definitions are MB per minute of runtime.
/// </summary>
public class QualityDefinitionSyncTests
{
    const string Definitions = """
    [
      {
        "quality": { "id": 19, "name": "Bluray-1080p", "source": "bluray", "resolution": 1080, "modifier": "none" },
        "title": "Bluray-1080p",
        "weight": 19,
        "minSize": 20,
        "maxSize": null,
        "preferredSize": null,
        "id": 19
      },
      {
        "quality": { "id": 18, "name": "WEBDL-2160p", "source": "webdl", "resolution": 2160, "modifier": "none" },
        "title": "WEBDL-2160p",
        "weight": 22,
        "minSize": 0,
        "maxSize": null,
        "preferredSize": null,
        "id": 26
      },
      {
        "quality": { "id": 31, "name": "Remux-2160p", "source": "bluray", "resolution": 2160, "modifier": "remux" },
        "title": "Remux-2160p",
        "weight": 24,
        "minSize": 90,
        "maxSize": null,
        "preferredSize": null,
        "id": 28
      }
    ]
    """;

    const string SonarrDefinitions = """
    [
      {
        "quality": { "id": 18, "name": "WEBDL-2160p", "source": "web", "resolution": 2160 },
        "title": "WEBDL-2160p",
        "weight": 16,
        "minSize": 0,
        "preferredSize": 95,
        "id": 20
      }
    ]
    """;

    const string DefinitionsAtFloor = """
    [
      {
        "quality": { "id": 19, "name": "Bluray-1080p", "source": "bluray", "resolution": 1080, "modifier": "none" },
        "title": "Bluray-1080p",
        "weight": 19,
        "minSize": 20,
        "maxSize": null,
        "preferredSize": null,
        "id": 19
      },
      {
        "quality": { "id": 18, "name": "WEBDL-2160p", "source": "webdl", "resolution": 2160, "modifier": "none" },
        "title": "WEBDL-2160p",
        "weight": 22,
        "minSize": 90,
        "maxSize": null,
        "preferredSize": null,
        "id": 26
      }
    ]
    """;

    const string DefinitionsWithTightCaps = """
    [
      {
        "quality": { "id": 18, "name": "WEBDL-2160p", "source": "webdl", "resolution": 2160, "modifier": "none" },
        "title": "WEBDL-2160p",
        "weight": 22,
        "minSize": 0,
        "maxSize": 70,
        "preferredSize": 80,
        "id": 26
      }
    ]
    """;

    static FakeHttpMessageHandler Handler(string definitions = Definitions) => new()
    {
        Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            request.Method == HttpMethod.Get ? definitions : "{}"))
    };

    static QualityDefinitionDto Sent(CapturedRequest request) =>
        JsonSerializer.Deserialize<QualityDefinitionDto>(
            request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Test]
    public async Task Mbps_IsConvertedToMbPerMinute()
    {
        await Assert.That(QualityDefinitionSync.MbpsToMbPerMinute(12)).IsEqualTo(90d);
        await Assert.That(QualityDefinitionSync.MbpsToMbPerMinute(4.86)).IsEqualTo(36.45d);
    }

    [Test]
    public async Task MinimumSize_ResolvesTheResolutionTier()
    {
        Dictionary<string, double> bitrates = new()
        {
            ["720p"] = 2,
            ["1080p"] = 0,
            ["2160p"] = 12
        };

        await Assert.That(QualityDefinitionSync.MinimumSizeMbPerMinute(2160, bitrates)).IsEqualTo(90d);
        await Assert.That(QualityDefinitionSync.MinimumSizeMbPerMinute(1080, bitrates)).IsNull();
        await Assert.That(QualityDefinitionSync.MinimumSizeMbPerMinute(720, bitrates)).IsEqualTo(15d);
    }

    [Test]
    public async Task Apply_SetsUhdFloor_AndLeavesOtherTiersAlone()
    {
        FakeHttpMessageHandler handler = Handler();
        RadarrClient client = new("http://localhost", "key", handler);
        QualityProfilesConfig config = new()
        {
            MinimumBitratesMbps = new() { ["2160p"] = 12 }
        };

        int changed = await QualityDefinitionSync.ApplyAsync(
            client, config, "Radarr", NullLogger.Instance);

        // WEBDL-2160p is below the floor; Remux-2160p is already at it; 1080p is unmanaged.
        await Assert.That(changed).IsEqualTo(1);

        CapturedRequest put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        await Assert.That(put.Uri.AbsolutePath).IsEqualTo("/api/v3/qualitydefinition/26");

        QualityDefinitionDto sent = Sent(put);
        await Assert.That(sent.MinSize).IsEqualTo(90d);
        await Assert.That(sent.MaxSize).IsNull();
        await Assert.That(sent.Quality.Name).IsEqualTo("WEBDL-2160p");
        await Assert.That(sent.Quality.Modifier).IsEqualTo("none");
        await Assert.That(sent.Title).IsEqualTo("WEBDL-2160p");
    }

    [Test]
    public async Task Apply_DefaultFloors_Update1080pAnd2160p()
    {
        FakeHttpMessageHandler handler = Handler();
        RadarrClient client = new("http://localhost", "key", handler);

        int changed = await QualityDefinitionSync.ApplyAsync(
            client, new QualityProfilesConfig(), "Radarr", NullLogger.Instance);

        // 1080p rises from 20 to 22.5 MB/min (3 Mbps); WEBDL-2160p rises to 90 (12 Mbps).
        await Assert.That(changed).IsEqualTo(2);

        List<QualityDefinitionDto> sent = handler.Requests
            .Where(r => r.Method == HttpMethod.Put)
            .Select(Sent)
            .ToList();

        await Assert.That(sent.Single(d => d.Quality.Name == "Bluray-1080p").MinSize)
            .IsEqualTo(22.5d);
        await Assert.That(sent.Single(d => d.Quality.Name == "WEBDL-2160p").MinSize)
            .IsEqualTo(90d);
    }

    [Test]
    public async Task Apply_PreservesRemuxModifierWhenItWrites()
    {
        FakeHttpMessageHandler handler = Handler();
        RadarrClient client = new("http://localhost", "key", handler);
        QualityProfilesConfig config = new()
        {
            MinimumBitratesMbps = new() { ["2160p"] = 30 }
        };

        int changed = await QualityDefinitionSync.ApplyAsync(
            client, config, "Radarr", NullLogger.Instance);

        await Assert.That(changed).IsEqualTo(2);

        List<QualityDefinitionDto> sent = handler.Requests
            .Where(r => r.Method == HttpMethod.Put)
            .Select(Sent)
            .ToList();

        QualityDefinitionDto remux = sent.Single(d => d.Quality.Name == "Remux-2160p");
        await Assert.That(remux.MinSize).IsEqualTo(225d);
        await Assert.That(remux.Quality.Modifier).IsEqualTo("remux");
        await Assert.That(remux.Quality.Resolution).IsEqualTo(2160);
    }

    [Test]
    public async Task Apply_RaisesPreferredAndClearsContradictoryMaximum()
    {
        FakeHttpMessageHandler handler = Handler(DefinitionsWithTightCaps);
        RadarrClient client = new("http://localhost", "key", handler);

        int changed = await QualityDefinitionSync.ApplyAsync(
            client, new QualityProfilesConfig(), "Radarr", NullLogger.Instance);

        await Assert.That(changed).IsEqualTo(1);

        QualityDefinitionDto sent = Sent(handler.Requests.Single(r => r.Method == HttpMethod.Put));
        await Assert.That(sent.MinSize).IsEqualTo(90d);
        await Assert.That(sent.PreferredSize).IsEqualTo(90d);
        await Assert.That(sent.MaxSize).IsNull();
    }

    [Test]
    public async Task Apply_SonarrPayloadWithoutModifier_DoesNotInventOne()
    {
        FakeHttpMessageHandler handler = Handler(SonarrDefinitions);
        SonarrClient client = new("http://localhost", "key", handler);

        int changed = await QualityDefinitionSync.ApplyAsync(
            client, new QualityProfilesConfig(), "Sonarr", NullLogger.Instance);

        await Assert.That(changed).IsEqualTo(1);

        CapturedRequest put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        await Assert.That(put.Body.Contains("modifier", StringComparison.OrdinalIgnoreCase)).IsFalse();

        QualityDefinitionDto sent = Sent(put);
        await Assert.That(sent.MinSize).IsEqualTo(90d);
        await Assert.That(sent.Quality.Name).IsEqualTo("WEBDL-2160p");
        await Assert.That(sent.Quality.Source).IsEqualTo("web");
    }

    [Test]
    public async Task Apply_DoesNotWriteWhenTheArrAlreadyMatches()
    {
        FakeHttpMessageHandler handler = Handler(DefinitionsAtFloor);
        RadarrClient client = new("http://localhost", "key", handler);
        QualityProfilesConfig config = new()
        {
            MinimumBitratesMbps = new() { ["2160p"] = 12, ["1080p"] = 20d * 8d / 60d }
        };

        int changed = await QualityDefinitionSync.ApplyAsync(
            client, config, "Radarr", NullLogger.Instance);

        // Both definitions already meet their floor.
        await Assert.That(changed).IsEqualTo(0);
        await Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Put)).IsFalse();
    }

    [Test]
    public async Task Apply_ZeroTierLeavesAnExistingFloorUntouched()
    {
        FakeHttpMessageHandler handler = Handler();
        RadarrClient client = new("http://localhost", "key", handler);
        QualityProfilesConfig config = new()
        {
            MinimumBitratesMbps = new() { ["1080p"] = 0, ["2160p"] = 0 }
        };

        int changed = await QualityDefinitionSync.ApplyAsync(
            client, config, "Radarr", NullLogger.Instance);

        await Assert.That(changed).IsEqualTo(0);
        await Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Put)).IsFalse();
    }
}
