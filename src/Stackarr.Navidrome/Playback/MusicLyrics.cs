namespace Stackarr.Navidrome.Playback;

/// <summary>One lyric line, with its start time in milliseconds when the source is synced.</summary>
public sealed record LyricsLine(long Start, string Text);

/// <summary>
/// One timed word or syllable inside a line, the karaoke "fill". <see cref="LineIndex"/> points at
/// the line it belongs to (the OpenSubsonic <c>cueLine.index</c>).
/// </summary>
public sealed record LyricsCue(int LineIndex, long Start, long End, string Text);

/// <summary>The main lyrics for a song, in the shape the player renders.</summary>
public sealed record LyricsTrack(
    string Kind,
    string Lang,
    bool Synced,
    int Offset,
    IReadOnlyList<LyricsLine> Lines,
    IReadOnlyList<LyricsCue> Cues);

/// <summary>Everything the in-site player needs to render a song's lyrics.</summary>
public sealed record LyricsResponse(string SongId, LyricsTrack Main);

/// <summary>
/// Flattens Navidrome's OpenSubsonic <c>lyricsList</c> into the player's own shape. Keeping a
/// Stackarr DTO here means the renderer is not coupled to Navidrome's payload (or its version);
/// only to what the player actually draws.
/// </summary>
public static class MusicLyricsMapper
{
    /// <summary>Null when there is nothing renderable (no lyrics, or lyrics with no lines).</summary>
    public static LyricsResponse? ToResponse(string songId, IReadOnlyList<SubsonicStructuredLyrics> tracks)
    {
        // Prefer the main lyrics; among equals take a synced one over a plain one.
        SubsonicStructuredLyrics? main = tracks
            .Where(t => IsMain(t.Kind))
            .OrderByDescending(t => t.Synced)
            .FirstOrDefault();

        if (main is null || main.Line.Count == 0)
            return null;

        List<LyricsLine> lines = [.. main.Line.Select(line => new LyricsLine(line.Start ?? 0, line.Value))];

        // Word-level cues are a parallel structure to the lines; flatten them, inheriting the
        // cueLine's bounds when an individual cue omits its own.
        List<LyricsCue> cues = [];
        foreach (SubsonicCueLine cueLine in main.CueLine)
        {
            foreach (SubsonicLyricCue cue in cueLine.Cue)
            {
                long start = cue.Start ?? cueLine.Start ?? 0;
                long end = cue.End ?? cueLine.End ?? start;
                cues.Add(new LyricsCue(cueLine.Index, start, end, cue.Value));
            }
        }

        LyricsTrack track = new(
            string.IsNullOrEmpty(main.Kind) ? "main" : main.Kind,
            main.Lang,
            main.Synced,
            main.Offset,
            lines,
            cues);
        return new LyricsResponse(songId, track);
    }

    static bool IsMain(string kind) =>
        string.IsNullOrEmpty(kind) || string.Equals(kind, "main", StringComparison.OrdinalIgnoreCase);
}
