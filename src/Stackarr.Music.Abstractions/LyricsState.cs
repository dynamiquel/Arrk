namespace Stackarr.Music.Abstractions;

/// <summary>
/// Where a track's lyrics lookup stands. Lyrics are cosmetic, so a genuine miss is never retried on a
/// timer, only a <see cref="Failed"/> lookup (the provider was unreachable) is cleared and retried.
/// </summary>
public enum LyricsState
{
    /// <summary>Never searched, the next lyrics sweep picks it up.</summary>
    None,

    /// <summary>Lyrics were found and written beside the audio file.</summary>
    Found,

    /// <summary>The provider reports a track with no lyrics (instrumental). Terminal.</summary>
    Instrumental,

    /// <summary>Searched but nothing is available. Retried only when the user asks.</summary>
    NotFound,

    /// <summary>The lookup itself failed (network error, provider outage). Cleared daily and retried.</summary>
    Failed
}

/// <summary>How well synced the lyrics we obtained are; decides whether the karaoke renderer can fill.</summary>
public enum LyricsTier
{
    None,

    /// <summary>Unsynchronised plain text, readable, but no highlighting.</summary>
    Plain,

    /// <summary>Line-level timestamps (LRC), the active line can be highlighted and followed.</summary>
    Line,

    /// <summary>Word/syllable timestamps (ELRC/TTML), karaoke word-by-word fill.</summary>
    Word
}
