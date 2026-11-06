using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Core.Services;
using Arrk.TestSupport;

namespace Arrk.Slskd.Tests;

/// <summary>
/// Verifies the app-facing slskd wrapper, in particular the failed-transfer cleanup that keeps the
/// unified Downloads page from silting up with dead entries.
/// </summary>
public class SlskdServiceTests
{
    static (SlskdService Service, FakeHttpMessageHandler Handler) Build(string downloadsJson, SlskdConfig? config = null)
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.EndsWith("/transfers/downloads")
                        ? downloadsJson
                        : """{ "ok": true }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        SlskdService service = new(
            new SlskdClient("http://slskd:5030", "u", "p", handler),
            new StaticOptionsMonitor<SlskdConfig>(config ?? new SlskdConfig { Enabled = true }),
            new ServiceRegistry(),
            NullLogger<SlskdService>.Instance);
        return (service, handler);
    }

    sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Test]
    public async Task RemoveFailedDownloads_RemovesOnlyFailuresOlderThanTheCutoff()
    {
        string old = DateTime.UtcNow.AddHours(-10).ToString("o");
        string recent = DateTime.UtcNow.AddMinutes(-5).ToString("o");
        string json = $$"""
          [ { "username": "peer1", "directories": [ { "directory": "Music\\Album", "files": [
              { "id": "old-fail", "filename": "Music\\Album\\1.flac", "state": "Completed, Errored", "percentComplete": 0, "size": 100, "endedAt": "{{old}}" },
              { "id": "recent-fail", "filename": "Music\\Album\\2.flac", "state": "Completed, Errored", "percentComplete": 0, "size": 100, "endedAt": "{{recent}}" },
              { "id": "old-ok", "filename": "Music\\Album\\3.flac", "state": "Completed, Succeeded", "percentComplete": 100, "size": 100, "endedAt": "{{old}}" } ] } ] } ]
          """;
        (SlskdService service, FakeHttpMessageHandler handler) = Build(json);

        int removed = await service.RemoveFailedDownloadsAsync(TimeSpan.FromHours(4));

        await Assert.That(removed).IsEqualTo(1);
        List<CapturedRequest> deletes = handler.Requests.Where(r => r.Method.Method == "DELETE").ToList();
        await Assert.That(deletes).HasCount(1);
        await Assert.That(deletes[0].Uri.AbsolutePath).Contains("old-fail");
        await Assert.That(deletes[0].Uri.Query).Contains("remove=true");
    }

    [Test]
    public async Task RemoveFailedDownloads_WhenDisabled_DoesNothing()
    {
        FakeHttpMessageHandler handler = new();
        SlskdService service = new(
            new SlskdClient("http://slskd:5030", "u", "p", handler),
            new StaticOptionsMonitor<SlskdConfig>(new SlskdConfig { Enabled = false }),
            new ServiceRegistry(),
            NullLogger<SlskdService>.Instance);

        int removed = await service.RemoveFailedDownloadsAsync(TimeSpan.FromHours(4));

        await Assert.That(removed).IsEqualTo(0);
        await Assert.That(handler.Requests).IsEmpty();
    }
}
