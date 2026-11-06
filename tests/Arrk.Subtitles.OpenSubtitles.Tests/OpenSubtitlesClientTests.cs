using System.Net;
using Arrk.Subtitles.OpenSubtitles;
using Arrk.TestSupport;

namespace Arrk.Subtitles.OpenSubtitles.Tests;

public class OpenSubtitlesClientTests
{
    const string SearchPayload = """
    {
      "data": [
        {
          "id": "1",
          "attributes": {
            "subtitle_id": "1",
            "language": "en",
            "download_count": 1200,
            "hearing_impaired": false,
            "foreign_parts_only": false,
            "moviehash_match": true,
            "format": "srt",
            "release": "Verity.2026.1080p.WEB-DL",
            "ratings": 8.4,
            "uploader": { "name": "someone" },
            "files": [ { "file_id": 12345, "file_name": "Verity.2026.1080p.srt" } ]
          }
        }
      ]
    }
    """;

    const string DownloadPayload = """
    {
      "link": "https://dl.example/abc",
      "file_name": "Verity.2026.1080p.srt",
      "remaining": 19,
      "requests": 1,
      "message": ""
    }
    """;

    static OpenSubtitlesClient Client(FakeHttpMessageHandler handler, bool credentials = false)
    {
        OpenSubtitlesConfig config = new()
        {
            ApiKey = "api-key",
            Username = credentials ? "user" : null,
            Password = credentials ? "pass" : null
        };
        return new OpenSubtitlesClient(new MutableOptionsMonitor<OpenSubtitlesConfig>(config), handler);
    }

    [Test]
    public async Task Search_SendsApiKeyAndBuildsTheQuery()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(SearchPayload);

        IReadOnlyList<OpenSubtitlesSubtitle> results = await Client(handler).SearchAsync(new OpenSubtitlesSearchQuery
        {
            ImdbId = "1234567",
            Query = "Verity",
            Year = 2026,
            Type = "movie",
            Languages = ["en", "fr"],
            MovieHash = "abcdef0123456789"
        });

        await Assert.That(results).HasSingleItem();
        await Assert.That(results[0].Attributes.Files[0].FileId).IsEqualTo(12345);

        CapturedRequest request = handler.Requests.Single();
        await Assert.That(request.Headers!["Api-Key"]).IsEqualTo("api-key");
        await Assert.That(request.Headers["User-Agent"]).StartsWith("Arrk/");
        await Assert.That(request.Uri.Query).Contains("imdb_id=1234567");
        await Assert.That(request.Uri.Query).Contains("languages=en%2Cfr");
        await Assert.That(request.Uri.Query).Contains("moviehash=abcdef0123456789");
        await Assert.That(request.Uri.Query).Contains("type=movie");
    }

    [Test]
    public async Task Download_LogsInAndUsesTheBearerToken()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{ "token": "jwt-token", "status": 200, "user": { "user_id": 1 } }""");
        handler.EnqueueJson(DownloadPayload);

        OpenSubtitlesDownloadResponse download = await Client(handler, credentials: true).DownloadAsync(12345);

        await Assert.That(download.Link).IsEqualTo("https://dl.example/abc");
        await Assert.That(download.Remaining).IsEqualTo(19);
        await Assert.That(handler.Requests).HasCount(2);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).EndsWith("/login");
        await Assert.That(handler.Requests[1].Uri.AbsolutePath).EndsWith("/download");
        await Assert.That(handler.Requests[1].Headers!["Authorization"]).IsEqualTo("Bearer jwt-token");
    }

    [Test]
    public async Task Download_DoesNotLoginWithoutCredentials()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(DownloadPayload);

        await Client(handler).DownloadAsync(12345);

        await Assert.That(handler.Requests).HasCount(1);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).EndsWith("/download");
        await Assert.That(handler.Requests[0].Headers!.ContainsKey("Authorization")).IsFalse();
    }

    [Test]
    public async Task Search_RetriesOnceAfterRateLimit()
    {
        FakeHttpMessageHandler handler = new();
        handler.Enqueue((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{ "message": "rate limited" }""", System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
            return Task.FromResult(response);
        });
        handler.EnqueueJson(SearchPayload);

        IReadOnlyList<OpenSubtitlesSubtitle> results = await Client(handler).SearchAsync(new OpenSubtitlesSearchQuery
        {
            ImdbId = "1234567",
            Languages = ["en"]
        });

        await Assert.That(results).HasSingleItem();
        await Assert.That(handler.Requests).HasCount(2);
    }

    [Test]
    public async Task MissingApiKey_IsNotConfigured()
    {
        FakeHttpMessageHandler handler = new();
        OpenSubtitlesClient client = new(
            new MutableOptionsMonitor<OpenSubtitlesConfig>(new OpenSubtitlesConfig()),
            handler);

        await Assert.That(client.IsConfigured).IsFalse();
        await Assert.That(await client.IsAvailableAsync()).IsFalse();
        await Assert.That(handler.Requests).IsEmpty();
    }
}
