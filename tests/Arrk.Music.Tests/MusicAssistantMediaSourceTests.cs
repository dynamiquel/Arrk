using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Ai;
using Arrk.Music.Services;

namespace Arrk.Music.Tests;

/// <summary>
/// The music media adapter's ownership rules. It only answers ids it produced (<c>music:…</c>) and
/// only searches music domains; the catalog and library behaviour is covered by the music suite.
/// </summary>
public class MusicAssistantMediaSourceTests
{
    static MusicAssistantMediaSource Source() => new(null!, NullLogger<MusicAssistantMediaSource>.Instance);

    [Test]
    public async Task OwnsTheMusicPrefix()
    {
        MusicAssistantMediaSource source = Source();

        await Assert.That(source.Key).IsEqualTo("music");
        await Assert.That(await source.GetAsync("tv:movie:1", null, default)).IsNull();
        await Assert.That(await source.GetAsync("music:album:0", null, default)).IsNull();
    }

}
