using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Ai;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// The TV media adapter's ownership rules. It only answers ids it produced (<c>tv:…</c>) and only
/// searches TV/movie domains; the rest of its behaviour is the catalog and arr services, which the
/// existing suites already cover.
/// </summary>
public class TvarrAssistantMediaSourceTests
{
    static TvarrAssistantMediaSource Source() => new(
        null!, null!, null!, null!, NullLogger<TvarrAssistantMediaSource>.Instance);

    [Test]
    public async Task OwnsTheTvPrefix()
    {
        TvarrAssistantMediaSource source = Source();

        await Assert.That(source.Key).IsEqualTo("tv");
        await Assert.That(await source.GetAsync("music:album:lib:1", null, default)).IsNull();
        await Assert.That(await source.GetAsync("tv:unknown:1", null, default)).IsNull();
    }

}
