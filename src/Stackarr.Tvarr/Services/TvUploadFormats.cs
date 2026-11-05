namespace Stackarr.Tvarr.Services;

/// <summary>
/// The file types Stackarr accepts for a local import. The list is deliberately the intersection
/// of what Radarr/Sonarr hand to Jellyfin and what Jellyfin can play without a transcode source
/// surprise; an unknown extension is rejected before a byte is written to the staging area.
/// </summary>
public static class TvUploadFormats
{
    static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts",
        ".mts", ".webm", ".flv", ".mpg", ".mpeg", ".ogv", ".3gp", ".vob"
    };

    static readonly HashSet<string> Subtitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup"
    };

    public static IReadOnlyCollection<string> VideoExtensions => Video;

    public static IReadOnlyCollection<string> SubtitleExtensions => Subtitles;

    /// <summary>The value for an <c>accept</c> attribute on the file picker.</summary>
    public static string Accept { get; } = string.Join(',', Video.Concat(Subtitles).OrderBy(extension => extension));

    /// <summary>Human readable list used in the upload dialog and API error messages.</summary>
    public static string SupportedLabel { get; } =
        string.Join(", ", Video.OrderBy(extension => extension).Select(extension => extension.TrimStart('.')));

    public static bool IsVideo(string fileName) => Video.Contains(Path.GetExtension(fileName));

    public static bool IsSubtitle(string fileName) => Subtitles.Contains(Path.GetExtension(fileName));

    public static bool IsSupported(string fileName) => IsVideo(fileName) || IsSubtitle(fileName);

    /// <summary>
    /// Converts a browser supplied file name into a safe leaf name for the staging area. The
    /// extension is preserved because the arr parsers use it, and directory traversal is removed.
    /// </summary>
    public static string SafeFileName(string fileName)
    {
        string leaf = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        if (string.IsNullOrWhiteSpace(leaf))
            leaf = "upload";

        foreach (char invalid in Path.GetInvalidFileNameChars())
            leaf = leaf.Replace(invalid, '_');

        leaf = leaf.Replace("..", "_");
        return leaf;
    }
}
