using Arrk.Jellyfin.Playback;

namespace Arrk.Jellyfin.Tests;

/// <summary>
/// Covers the subtitle lift: Jellyfin's WebVTT cues are bottom-anchored and collide with Arrk's
/// controls, so the proxy raises them unless a cue already chose its own line.
/// </summary>
public class WebVttRewriterTests
{
    [Test]
    public async Task RaiseCues_AddsAPositionToBottomAnchoredCues()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:02.000
            First line

            00:00:03.000 --> 00:00:04.000
            Second line
            """;

        string result = WebVttRewriter.RaiseCues(vtt);

        await Assert.That(result).Contains("00:00:01.000 --> 00:00:02.000 line:86%");
        await Assert.That(result).Contains("00:00:03.000 --> 00:00:04.000 line:86%");
        await Assert.That(result).Contains("First line");
    }

    [Test]
    public async Task RaiseCues_LeavesCuesWithTheirOwnLine()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:02.000 line:10% align:start
            A sign at the top
            """;

        string result = WebVttRewriter.RaiseCues(vtt);

        await Assert.That(result).Contains("line:10% align:start");
        await Assert.That(result).DoesNotContain("line:86%");
    }

    [Test]
    public async Task RaiseCues_DoesNotTouchNoteOrStyleBlocks()
    {
        const string vtt = """
            WEBVTT

            NOTE this --> is not a cue

            STYLE
            ::cue { color: yellow; }

            00:00:01.000 --> 00:00:02.000
            A cue
            """;

        string result = WebVttRewriter.RaiseCues(vtt);

        await Assert.That(result).Contains("NOTE this --> is not a cue");
        await Assert.That(result).Contains("::cue { color: yellow; }");
        await Assert.That(result).Contains("00:00:01.000 --> 00:00:02.000 line:86%");
    }

    [Test]
    public async Task IsWebVtt_DetectsContentTypeAndExtension()
    {
        await Assert.That(WebVttRewriter.IsWebVtt("text/vtt; charset=utf-8", null)).IsTrue();
        await Assert.That(WebVttRewriter.IsWebVtt(null, "Videos/item/Subtitles/2/Stream.vtt")).IsTrue();
        await Assert.That(WebVttRewriter.IsWebVtt("video/mp4", "segment.mp4")).IsFalse();
    }
}
