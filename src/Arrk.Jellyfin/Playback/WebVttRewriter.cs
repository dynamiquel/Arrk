namespace Arrk.Jellyfin.Playback;

/// <summary>
/// Lifts side-loaded WebVTT cues above the player control bar. Jellyfin's converter emits
/// bottom-anchored cues, which collide with the controls in every browser. Cues that already set
/// their own <c>line</c> (signs, top text) are left untouched, as are NOTE/STYLE/REGION blocks.
/// </summary>
public static class WebVttRewriter
{
    /// <summary>Where the bottom-anchored cue line sits, as a percentage from the top.</summary>
    public const int DefaultLinePercent = 86;

    public static string RaiseCues(string vtt, int linePercent = DefaultLinePercent)
    {
        if (string.IsNullOrEmpty(vtt))
            return vtt;

        string[] lines = vtt
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        bool inBlock = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            if (!inBlock &&
                (line.StartsWith("NOTE", StringComparison.Ordinal) ||
                 line.StartsWith("STYLE", StringComparison.Ordinal) ||
                 line.StartsWith("REGION", StringComparison.Ordinal)))
            {
                inBlock = true;
                continue;
            }

            if (inBlock)
            {
                if (line.Length == 0)
                    inBlock = false;
                continue;
            }

            if (!line.Contains("-->", StringComparison.Ordinal))
                continue;

            if (line.Contains(" line:", StringComparison.OrdinalIgnoreCase))
                continue;

            lines[i] = line + $" line:{linePercent}%";
        }

        return string.Join("\n", lines);
    }

    public static bool IsWebVtt(string? contentType, string? path) =>
        (contentType?.StartsWith("text/vtt", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (path?.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ?? false);
}
