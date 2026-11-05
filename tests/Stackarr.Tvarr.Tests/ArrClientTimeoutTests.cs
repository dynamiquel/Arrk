using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// Sonarr's series lookup proxies TVDB and is intermittently slow, which surfaced in the UI as
/// "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing".
/// Sonarr therefore allows a full minute while Radarr keeps the arr default.
/// </summary>
public class ArrClientTimeoutTests
{
    [Test]
    public async Task SonarrClient_UsesOneMinuteTimeout()
    {
        SonarrClient client = new("http://sonarr:8989/sonarr", "stackarr");

        await Assert.That(client.RequestTimeout).IsEqualTo(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task RadarrClient_KeepsArrDefaultTimeout()
    {
        RadarrClient client = new("http://radarr:7878/radarr", "stackarr");

        await Assert.That(ArrClient.DefaultRequestTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(client.RequestTimeout).IsEqualTo(ArrClient.DefaultRequestTimeout);
    }

    /// <summary>
    /// Proves the configured timeout is the one actually applied to the underlying
    /// <see cref="HttpClient"/>: a response slower than the timeout is cancelled instead of
    /// hanging. A short custom timeout and an injected handler let us assert this in milliseconds
    /// rather than waiting 30s.
    /// </summary>
    [Test]
    public async Task ConfiguredTimeout_IsEnforcedOnRequests()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueDelay(TimeSpan.FromSeconds(5), "{}");
        ArrClient client = new(
            "http://sonarr:8989/sonarr", "stackarr", handler, TimeSpan.FromMilliseconds(250));

        await Assert.That(async () => await client.GetSystemStatus()).Throws<TaskCanceledException>();
    }
}
