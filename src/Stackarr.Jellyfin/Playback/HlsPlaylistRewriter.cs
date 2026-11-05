using System.Text.RegularExpressions;

namespace Stackarr.Jellyfin.Playback;

/// <summary>
/// Removes the Jellyfin access token from an HLS playlist before it is handed to the browser.
/// Jellyfin embeds <c>ApiKey=...</c> in every URI (including <c>#EXT-X-MAP</c>), and the browser
/// must never see it, the stream proxy injects the token server-side on every request instead.
/// </summary>
/// <remarks>
/// The transform is narrow: it only strips the auth parameter. All other query
/// parameters (DeviceId, PlaySessionId, ...) are preserved because Jellyfin needs them to find the
/// transcode session, and the URIs are left relative so they resolve against the proxy base path.
/// </remarks>
public static partial class HlsPlaylistRewriter
{
    /// <summary>Auth parameter that is not the first query parameter (preceded by &amp;).</summary>
    [GeneratedRegex(@"&api_?key=[^&\s""']*", RegexOptions.IgnoreCase)]
    private static partial Regex AuthAfterAmp();

    /// <summary>Auth parameter first in the query (preceded by ?) with more parameters following.</summary>
    [GeneratedRegex(@"\?api_?key=[^&\s""']*&", RegexOptions.IgnoreCase)]
    private static partial Regex AuthFirstThenMore();

    /// <summary>Auth parameter that is the only query parameter.</summary>
    [GeneratedRegex(@"\?api_?key=[^&\s""']*", RegexOptions.IgnoreCase)]
    private static partial Regex AuthOnly();

    /// <summary>Strips <c>ApiKey</c>/<c>api_key</c> query parameters from every URI in the playlist.</summary>
    public static string StripAuthToken(string playlist)
    {
        if (string.IsNullOrEmpty(playlist))
            return playlist;

        // Order matters: non-first, then first-with-following, then first-and-only. This avoids
        // leaving a dangling '?' or '&' behind when the stripped parameter was first or last.
        string result = AuthAfterAmp().Replace(playlist, "");
        result = AuthFirstThenMore().Replace(result, "?");
        result = AuthOnly().Replace(result, "");
        return result;
    }

    /// <summary>True when a response is an HLS playlist and therefore needs token stripping.</summary>
    public static bool IsPlaylist(string? contentType, string? path)
    {
        if (path is not null && path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrEmpty(contentType))
            return false;

        return contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("x-mpegurl", StringComparison.OrdinalIgnoreCase);
    }
}
