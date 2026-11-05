using Stackarr.Music.Services;

namespace Stackarr.Music.Tests;

/// <summary>
/// Covers the mini-player playlist queue rules: a preview (a Deezer clip for a song we don't hold) must
/// never enter the album/playlist queue, so playing a playlist never "suddenly" plays a preview when
/// it advances to a missing song. Previews play only when the user explicitly picks one.
/// </summary>
public class PlaylistPlaybackTests
{
    static PlaylistTrack Held(string title, string songId) =>
        new(title, "Artist", "Album", songId, "http://cdn/preview.mp3", 0, 0, "http://cdn/cover.jpg");

    static PlaylistTrack PreviewOnly(string title) =>
        new(title, "Artist", "Album", "", "http://cdn/preview.mp3", 0, 0, "http://cdn/cover.jpg");

    static PlaylistTrack Unplayable(string title) =>
        new(title, "Artist", "Album", "", "", 0, 0, "");

    [Test]
    public async Task LibraryQueue_ExcludesPreviewOnlyTracks()
    {
        List<PlaylistTrack> tracks =
        [
            Held("A", "s1"),
            PreviewOnly("B"),
            Held("C", "s3"),
            Unplayable("D")
        ];

        List<PlaylistTrack> queue = PlaylistPlayback.LibraryQueue(tracks);

        await Assert.That(queue.Select(t => t.Title)).IsEquivalentTo(new[] { "A", "C" });
        await Assert.That(queue.All(t => t.InLibrary)).IsTrue();
    }

    [Test]
    public async Task ForTrack_HeldSong_StartsTheLibraryQueueAtItsPosition()
    {
        List<PlaylistTrack> tracks = [Held("A", "s1"), PreviewOnly("B"), Held("C", "s3")];

        (List<PlaylistTrack> queue, int index) = PlaylistPlayback.ForTrack(tracks, tracks[2]);

        await Assert.That(queue.Select(t => t.Title)).IsEquivalentTo(new[] { "A", "C" });
        await Assert.That(index).IsEqualTo(1);
        // The queue is library-only, so prev/next can never land on the preview.
        await Assert.That(queue.Any(t => t.Title == "B")).IsFalse();
    }

    [Test]
    public async Task ForTrack_Preview_IsAOneOffQueue()
    {
        List<PlaylistTrack> tracks = [Held("A", "s1"), PreviewOnly("B")];

        (List<PlaylistTrack> queue, int index) = PlaylistPlayback.ForTrack(tracks, tracks[1]);

        await Assert.That(index).IsEqualTo(0);
        await Assert.That(queue.Count).IsEqualTo(1);
        await Assert.That(queue[0].Title).IsEqualTo("B");
        await Assert.That(queue[0].InLibrary).IsFalse();
    }

    [Test]
    public async Task ForTrack_Unplayable_ProducesNoQueue()
    {
        List<PlaylistTrack> tracks = [Unplayable("A")];

        (List<PlaylistTrack> queue, _) = PlaylistPlayback.ForTrack(tracks, tracks[0]);

        await Assert.That(queue).IsEmpty();
    }
}
