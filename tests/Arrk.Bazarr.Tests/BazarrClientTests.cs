using Arrk.TestSupport;

namespace Arrk.Bazarr.Tests;

public class BazarrClientTests
{
    const string MoviePayload = """
    {
      "data": [
        {
          "radarrId": 1273221,
          "title": "Scary Movie",
          "year": 2026,
          "path": "/movies/1273221/Scary.Movie.mkv",
          "monitored": true,
          "profileId": 1,
          "missing_subtitles": [
            { "name": "French", "code2": "fr", "code3": "fra", "forced": false, "hi": false }
          ],
          "subtitles": [
            { "name": "English", "code2": "en", "code3": "eng", "path": "/movies/a.en.srt", "forced": false, "hi": false }
          ]
        }
      ],
      "total": 1
    }
    """;

    const string SearchPayload = """
    {
      "data": [
        {
          "provider": "gestdown",
          "subtitle": "abc123",
          "language": "French",
          "score": 93,
          "orig_score": 95,
          "score_without_hash": 90,
          "release_info": [ "Scary.Movie.2026.1080p", "WEB-DL" ],
          "url": "https://example.invalid/subtitle",
          "uploader": "someone",
          "hearing_impaired": "False",
          "forced": "False",
          "original_format": "False",
          "matches": [ "title" ]
        }
      ]
    }
    """;

    static BazarrClient Client(FakeHttpMessageHandler handler) =>
        new("http://bazarr:6767", _ => Task.FromResult<string?>("secret-key"), handler);

    [Test]
    public async Task GetMovies_ParsesEnvelopeAndSendsApiKey()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(MoviePayload);

        List<BazarrMovie> movies = await Client(handler).GetMoviesAsync([1273221]);

        await Assert.That(movies).HasSingleItem();
        await Assert.That(movies[0].RadarrId).IsEqualTo(1273221);
        await Assert.That(movies[0].Subtitles.Single().Code2).IsEqualTo("en");
        await Assert.That(movies[0].MissingSubtitles.Single().Code2).IsEqualTo("fr");

        CapturedRequest request = handler.Requests.Single();
        await Assert.That(request.Uri.Query).Contains("radarrid");
        await Assert.That(request.Headers!["X-API-KEY"]).IsEqualTo("secret-key");
    }

    [Test]
    public async Task SearchMovie_ParsesManualProviderCandidate()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(SearchPayload);

        List<BazarrProviderCandidate> candidates = await Client(handler).SearchMovieAsync(1273221);

        await Assert.That(candidates).HasSingleItem();
        await Assert.That(candidates[0].Provider).IsEqualTo("gestdown");
        await Assert.That(candidates[0].Subtitle).IsEqualTo("abc123");
        await Assert.That(candidates[0].Language).IsEqualTo("French");
        await Assert.That(candidates[0].Score).IsEqualTo(93);
        await Assert.That(candidates[0].HearingImpaired).IsEqualTo("False");
    }

    [Test]
    public async Task DownloadMovieSubtitle_PostsProviderAndCandidateId()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(System.Net.HttpStatusCode.NoContent);

        BazarrProviderCandidate candidate = new()
        {
            Provider = "gestdown",
            Subtitle = "abc123",
            HearingImpaired = "False",
            Forced = "True",
            OriginalFormat = "False"
        };

        await Client(handler).DownloadMovieSubtitleAsync(1273221, candidate);

        CapturedRequest request = handler.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.Body).Contains("radarrid=1273221");
        await Assert.That(request.Body).Contains("provider=gestdown");
        await Assert.That(request.Body).Contains("subtitle=abc123");
        await Assert.That(request.Body).Contains("forced=True");
    }

    [Test]
    public async Task UpdateSettings_PostsRepeatedListFields()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(System.Net.HttpStatusCode.NoContent);

        List<KeyValuePair<string, string>> fields =
        [
            new("languages-enabled", "en"),
            new("languages-enabled", "fr"),
            new("settings-general-enabled_providers", "gestdown"),
            new("settings-general-enabled_providers", "subtitlecat")
        ];

        await Client(handler).UpdateSettingsAsync(fields);

        string body = handler.Requests.Single().Body;
        await Assert.That(body).Contains("languages-enabled=en");
        await Assert.That(body).Contains("languages-enabled=fr");
        await Assert.That(body).Contains("settings-general-enabled_providers=gestdown");
        await Assert.That(body).Contains("settings-general-enabled_providers=subtitlecat");
    }
}
