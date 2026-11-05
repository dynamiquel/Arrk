using Stackarr.TestSupport;

namespace Stackarr.Music.Tests;

/// <summary>
/// Guards the two lyrics invariants that are easy to break and expensive to notice:
/// the karaoke work stays in JavaScript off the audio clock (Blazor must never render a frame at
/// 60 Hz), and lyrics are proxied through the tokenised player route instead of handing the browser
/// Navidrome's credentials.
/// </summary>
public class LyricsConventionsTests
{
    [Test]
    public async Task LyricsRunInJavaScript_NotInBlazorRenderCycles()
    {
        string engine = RepoPaths.ReadSource("src/Stackarr.Music/wwwroot/js/stackarr-lyrics.js");
        string player = RepoPaths.ReadSource("src/Stackarr.Music/wwwroot/js/stackarr-music.js");

        await Assert.That(engine).Contains("requestAnimationFrame");
        await Assert.That(engine).Contains("cancelAnimationFrame");
        await Assert.That(player).Contains("getTime:");
        await Assert.That(player).Contains("stackarrLyrics.attach");
        await Assert.That(player).DoesNotContain("invokeMethodAsync('LyricsTick'");
    }

    [Test]
    public async Task ThePlayerKeepsOneAudioElement()
    {
        // The mini bar and the full-screen view are two surfaces over the same playback; a second
        // Audio element would play the track twice and break the shared time source.
        string player = RepoPaths.ReadSource("src/Stackarr.Music/wwwroot/js/stackarr-music.js");

        await Assert.That(CountOccurrences(player, "new Audio(")).IsEqualTo(1);
    }

    [Test]
    public async Task LyricsAreProxiedThroughTheTokenisedPlayerRoute()
    {
        string endpoints = RepoPaths.ReadSource("src/Stackarr.Navidrome/Playback/NavidromePlaybackEndpoints.cs");

        await Assert.That(endpoints).Contains("MapGet(\"/lyrics/{songId}\"");
        await Assert.That(endpoints).Contains("MusicLyricsMapper.ToResponse");
    }

    static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
