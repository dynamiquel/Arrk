using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.TestSupport;

namespace Arrk.Bazarr.Tests;

public class BazarrSubtitleProviderTests
{
    const string MoviePayload = """
    {
      "data": [{
        "radarrId": 5,
        "title": "Verity",
        "year": 2026,
        "path": "/movies/5/Verity.mkv",
        "monitored": true,
        "profileId": 1,
        "missing_subtitles": [
          { "name": "French", "code2": "fr", "code3": "fra", "forced": false, "hi": false },
          { "name": "German", "code2": "de", "code3": "deu", "forced": false, "hi": false }
        ],
        "subtitles": [
          { "name": "English", "code2": "en", "code3": "eng", "path": "/movies/5/Verity.en.srt", "forced": false, "hi": false }
        ]
      }]
    }
    """;

    const string MoviePayloadWithFrench = """
    {
      "data": [{
        "radarrId": 5,
        "title": "Verity",
        "year": 2026,
        "path": "/movies/5/Verity.mkv",
        "monitored": true,
        "profileId": 1,
        "missing_subtitles": [
          { "name": "German", "code2": "de", "code3": "deu", "forced": false, "hi": false }
        ],
        "subtitles": [
          { "name": "French", "code2": "fr", "code3": "fra", "path": "/movies/5/Verity.fr.srt", "forced": false, "hi": false },
          { "name": "English", "code2": "en", "code3": "eng", "path": "/movies/5/Verity.en.srt", "forced": false, "hi": false }
        ]
      }]
    }
    """;

    const string SearchPayload = """
    {
      "data": [
        {
          "provider": "gestdown",
          "subtitle": "fr-best",
          "language": "French",
          "score": 95,
          "release_info": [ "Verity.2026.1080p" ],
          "hearing_impaired": "False",
          "forced": "False",
          "original_format": "False"
        },
        {
          "provider": "subtitlecat",
          "subtitle": "de-best",
          "language": "German",
          "score": 80,
          "release_info": [ "Verity.2026.1080p" ],
          "hearing_impaired": "False",
          "forced": "False",
          "original_format": "False"
        },
        {
          "provider": "gestdown",
          "subtitle": "en-best",
          "language": "English",
          "score": 99,
          "release_info": [ "Verity.2026.1080p" ],
          "hearing_impaired": "False",
          "forced": "False",
          "original_format": "False"
        }
      ]
    }
    """;

    static ArrkInstance Instance() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "abc" })
            .Build());

    static BazarrSubtitleProvider Provider(
        FakeHttpMessageHandler handler,
        IEventBus? bus = null,
        BazarrConfig? config = null,
        SubtitlesConfig? subtitlesConfig = null)
    {
        BazarrClient client = new("http://bazarr:6767", _ => Task.FromResult<string?>("key"), handler);
        BazarrSetupService setup = new(client, Instance(), null!, null!);
        return new BazarrSubtitleProvider(
            client,
            setup,
            bus ?? new EventBus(),
            new MutableOptionsMonitor<BazarrConfig>(config ?? new BazarrConfig()),
            new MutableOptionsMonitor<SubtitlesConfig>(subtitlesConfig ?? new SubtitlesConfig()),
            NullLogger<BazarrSubtitleProvider>.Instance);
    }

    [Test]
    public async Task Provider_IsEnabledFromBazarrConfig()
    {
        BazarrConfig config = new() { Enabled = true, Providers = ["gestdown"] };

        BazarrSubtitleProvider provider = Provider(new FakeHttpMessageHandler(), config: config);

        await Assert.That(provider.IsEnabled).IsTrue();
        await Assert.That(provider.Priority).IsEqualTo(1);
    }

    const string ProfilesPayload = """
    [
      {
        "profileId": 1,
        "name": "Arrk Default",
        "cutoff": 1,
        "items": [
          { "id": 1, "language": "en", "audio_exclude": "False", "audio_only_include": "False", "hi": "False", "forced": "False" }
        ],
        "mustContain": [],
        "mustNotContain": [],
        "originalFormat": null,
        "tag": null
      }
    ]
    """;

    [Test]
    public async Task Search_FiltersToRequestedLanguagesAndMapsCandidates()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(ProfilesPayload);
        handler.EnqueueJson(MoviePayload);
        handler.EnqueueStatus(System.Net.HttpStatusCode.NoContent);
        handler.EnqueueStatus(System.Net.HttpStatusCode.NoContent);
        handler.EnqueueJson(SearchPayload);
        handler.EnqueueStatus(System.Net.HttpStatusCode.NoContent);

        IReadOnlyList<SubtitleCandidate> candidates = await Provider(handler).SearchAsync(
            new SubtitleSearchRequest(SubtitleTarget.Movie(5), ["fr"]));

        await Assert.That(candidates).HasCount(1);
        await Assert.That(candidates[0].Id).IsEqualTo("fr-best");
        await Assert.That(candidates[0].LanguageCode).IsEqualTo("fr");
        await Assert.That(candidates[0].Score).IsEqualTo(95);
        await Assert.That(candidates[0].Title).Contains("Verity.2026.1080p");
    }

    [Test]
    public async Task Search_AcceptsBazarrLanguageCodesAsWellAsNames()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""
        {
          "data": [
            {
              "provider": "subf2m",
              "subtitle": "fr-code",
              "language": "fr",
              "score": 88,
              "release_info": [ "Verity.2026.1080p" ],
              "hearing_impaired": "False",
              "forced": "False",
              "original_format": "False"
            }
          ]
        }
        """);

        SubtitlesConfig subtitlesConfig = new() { Languages = ["en", "fr"] };
        IReadOnlyList<SubtitleCandidate> candidates = await Provider(handler, subtitlesConfig: subtitlesConfig).SearchAsync(
            new SubtitleSearchRequest(SubtitleTarget.Movie(5), ["fr"]));

        await Assert.That(candidates).HasCount(1);
        await Assert.That(candidates[0].LanguageCode).IsEqualTo("fr");
        await Assert.That(candidates[0].Id).IsEqualTo("fr-code");
    }

    [Test]
    public async Task Download_PublishesEventAfterBazarrReportsTheTrack()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(System.Net.HttpStatusCode.NoContent);
        handler.EnqueueJson(MoviePayloadWithFrench);

        EventBus bus = new();
        List<SubtitleDownloadedEvent> events = [];
        using IDisposable subscription = bus.Subscribe<SubtitleDownloadedEvent>((evt, _) =>
        {
            events.Add(evt);
            return ValueTask.CompletedTask;
        });

        await Provider(handler, bus).DownloadAsync(new SubtitleDownloadRequest(
            SubtitleTarget.Movie(5, "Verity"),
            new SubtitleCandidate
            {
                Provider = "gestdown",
                Id = "fr-best",
                LanguageCode = "fr",
                LanguageName = "French"
            }));

        await Assert.That(events).HasCount(1);
        await Assert.That(events[0].Language).IsEqualTo("fr");
        await Assert.That(events[0].MovieId).IsEqualTo(5);
    }

}
