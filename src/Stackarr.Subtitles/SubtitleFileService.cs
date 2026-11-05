using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Stackarr.Media.Abstractions;

namespace Stackarr.Subtitles;

/// <summary>
/// Locates video files, discovers existing subtitle tracks and writes sidecar subtitle files.
/// All paths are resolved through the TV module's arr path mapper so a container path such as
/// <c>/movies/5/Verity.mkv</c> becomes a path this process can read.
/// </summary>
public sealed partial class SubtitleFileService(
    IEnumerable<IMediaPathMapper> pathMappers,
    IEnumerable<IMediaProbe> probes)
{
    static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub"
    };

    static readonly HashSet<string> FlagTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "forced", "hi", "sdh", "cc", "default", "foreign", "commentary", "signs", "songs"
    };

    [GeneratedRegex("^[a-zA-Z]{2,3}(?:-[a-zA-Z]{2,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageToken();

    public string? ResolveLocalPath(string? arrPath)
    {
        if (string.IsNullOrWhiteSpace(arrPath))
            return null;

        foreach (IMediaPathMapper mapper in pathMappers)
        {
            string? mapped = mapper.MapToLocalPath(arrPath);
            if (!string.IsNullOrWhiteSpace(mapped))
                return mapped;
        }

        return File.Exists(arrPath) ? arrPath : null;
    }

    public async Task<IReadOnlyList<SubtitleTrack>> GetTracksAsync(
        string? arrPath,
        MediaTechnicalInfo? fallback,
        CancellationToken ct = default,
        bool probeEmbedded = true)
    {
        List<SubtitleTrack> tracks = [];
        string? localPath = ResolveLocalPath(arrPath);

        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            tracks.AddRange(ReadSidecars(localPath));

            IMediaProbe? probe = probeEmbedded ? probes.FirstOrDefault() : null;
            if (probe is not null)
            {
                try
                {
                    MediaTechnicalInfo? probed = await probe.ProbeAsync(localPath, ct);
                    if (probed is not null)
                        tracks.AddRange(ReadTechnical(probed, localPath));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed probe falls back to the library metadata below.
                }
            }
        }

        if (fallback is not null)
            tracks.AddRange(ReadTechnical(fallback, localPath ?? arrPath ?? ""));

        return tracks
            .Where(track => track.LanguageCode.Length > 0)
            .DistinctBy(track => (track.LanguageCode, track.Forced, track.HearingImpaired))
            .ToList();
    }

    public async Task<string> WriteAsync(
        string arrPath,
        string languageCode,
        bool forced,
        bool hearingImpaired,
        byte[] payload,
        string? downloadFileName,
        bool overwrite,
        CancellationToken ct = default)
    {
        string? localPath = ResolveLocalPath(arrPath);
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            throw new InvalidOperationException(
                "Stackarr cannot see this media file. Check that the media storage root is mounted in the Stackarr container.");

        (byte[] content, string extension) = ExtractSubtitle(payload, downloadFileName, languageCode);
        string baseName = Path.GetFileNameWithoutExtension(localPath);
        string suffix = $".{languageCode}";
        if (forced)
            suffix += ".forced";
        if (hearingImpaired)
            suffix += ".hi";
        string directory = Path.GetDirectoryName(localPath) ?? ".";
        string target = Path.Combine(directory, baseName + suffix + extension);

        if (File.Exists(target) && !overwrite)
            return target;

        string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, content, ct);
            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // Best effort cleanup; the target write already succeeded or failed.
                }
            }
        }
    }

    public async Task<string?> ComputeHashAsync(string? arrPath, CancellationToken ct = default)
    {
        string? localPath = ResolveLocalPath(arrPath);
        if (string.IsNullOrWhiteSpace(localPath))
            return null;

        FileInfo file;
        try
        {
            file = new FileInfo(localPath);
            if (!file.Exists || file.Length < 128 * 1024)
                return null;
        }
        catch
        {
            return null;
        }

        try
        {
            await using FileStream stream = new(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] first = new byte[64 * 1024];
            byte[] last = new byte[64 * 1024];
            await stream.ReadExactlyAsync(first, ct);
            stream.Seek(-64 * 1024, SeekOrigin.End);
            await stream.ReadExactlyAsync(last, ct);

            ulong hash = (ulong)file.Length;
            hash += SumWords(first);
            hash += SumWords(last);
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static ulong SumWords(byte[] buffer)
    {
        ulong sum = 0;
        for (int index = 0; index < buffer.Length; index += 8)
            sum += BitConverter.ToUInt64(buffer, index);
        return sum;
    }

    static IEnumerable<SubtitleTrack> ReadSidecars(string videoPath)
    {
        string directory = Path.GetDirectoryName(videoPath) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(videoPath);
        if (!Directory.Exists(directory))
            yield break;

        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(directory)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string path in files)
        {
            string extension = Path.GetExtension(path);
            if (!SubtitleExtensions.Contains(extension))
                continue;

            string fileName = Path.GetFileName(path);
            if (!fileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            string remainder = fileName[(baseName.Length + 1)..];
            string withoutExtension = remainder[..^extension.Length];
            string[] tokens = withoutExtension.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string? language = tokens
                .Select(token => SubtitleLanguages.NormalizeCode(token))
                .FirstOrDefault(token => LanguageToken().IsMatch(token) && !FlagTokens.Contains(token));

            if (language is null && tokens.Any(token => string.Equals(token, "hi", StringComparison.OrdinalIgnoreCase)))
                language = "hi";

            if (string.IsNullOrWhiteSpace(language))
                continue;

            string code = SubtitleLanguages.ToTwoLetter(language);
            bool forced = tokens.Any(token => string.Equals(token, "forced", StringComparison.OrdinalIgnoreCase));
            bool hi = tokens.Any(token =>
                string.Equals(token, "hi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "sdh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "cc", StringComparison.OrdinalIgnoreCase));

            yield return new SubtitleTrack(code, SubtitleLanguages.DisplayName(code), forced, hi, path);
        }
    }

    static IEnumerable<SubtitleTrack> ReadTechnical(MediaTechnicalInfo technical, string path)
    {
        foreach (SubtitleStreamInfo subtitle in technical.Subtitles)
        {
            string code = SubtitleLanguages.ToTwoLetter(subtitle.Language);
            if (code.Length == 0)
                code = CodeFromName(subtitle.LanguageName);

            if (code.Length == 0)
                continue;

            bool forced = subtitle.IsForced
                          || ContainsToken(subtitle.Title, "forced");
            bool hi = ContainsToken(subtitle.Title, "sdh")
                      || ContainsToken(subtitle.Title, "hearing")
                      || ContainsToken(subtitle.Title, "hi");

            yield return new SubtitleTrack(
                code,
                string.IsNullOrWhiteSpace(subtitle.LanguageName)
                    ? SubtitleLanguages.DisplayName(code)
                    : subtitle.LanguageName,
                forced,
                hi,
                path);
        }
    }

    static bool ContainsToken(string value, string token) =>
        value.Split([' ', '.', '_', '-', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));

    static string CodeFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        string normalized = SubtitleLanguages.NormalizeCode(name);
        if (normalized.Length is 2 or 3)
            return SubtitleLanguages.ToTwoLetter(normalized);

        CultureInfo? culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(item => string.Equals(item.EnglishName, name, StringComparison.OrdinalIgnoreCase));
        return culture is null ? "" : SubtitleLanguages.ToTwoLetter(culture.TwoLetterISOLanguageName);
    }

    static (byte[] Content, string Extension) ExtractSubtitle(
        byte[] payload,
        string? fileName,
        string languageCode)
    {
        byte[] content = payload;
        string name = fileName ?? "";

        if (IsZip(payload) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            (content, name) = ExtractZip(payload, languageCode);
        }
        else if (IsGzip(payload) || name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            content = ExtractGzip(payload);
            name = Path.GetFileNameWithoutExtension(name);
        }

        string extension = Path.GetExtension(name);
        if (!SubtitleExtensions.Contains(extension))
            extension = ".srt";

        return (NormalizeEncoding(content), extension);
    }

    static (byte[] Content, string Name) ExtractZip(byte[] payload, string languageCode)
    {
        using MemoryStream stream = new(payload);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);

        List<ZipArchiveEntry> entries = [.. archive.Entries
            .Where(entry => SubtitleExtensions.Contains(Path.GetExtension(entry.FullName)))
            .OrderByDescending(entry => Path.GetExtension(entry.FullName).Equals(".srt", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.Name.Contains($".{languageCode}.", StringComparison.OrdinalIgnoreCase))];

        ZipArchiveEntry? entry = entries.FirstOrDefault();
        if (entry is null)
            throw new InvalidOperationException("The downloaded archive did not contain a subtitle file.");

        using Stream entryStream = entry.Open();
        using MemoryStream output = new();
        entryStream.CopyTo(output);
        return (output.ToArray(), entry.Name);
    }

    static byte[] ExtractGzip(byte[] payload)
    {
        using MemoryStream input = new(payload);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    static bool IsZip(byte[] payload) =>
        payload.Length >= 4 && payload[0] == 0x50 && payload[1] == 0x4B && payload[2] == 0x03 && payload[3] == 0x04;

    static bool IsGzip(byte[] payload) =>
        payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B;

    static byte[] NormalizeEncoding(byte[] content)
    {
        if (content.Length == 0)
            return content;

        try
        {
            _ = new UTF8Encoding(false, true).GetString(content);
            return content;
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: most subtitle files in the wild are Windows-1252/Latin-1.
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string text = Encoding.GetEncoding(1252).GetString(content);
            return Encoding.UTF8.GetBytes(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            string text = Encoding.Latin1.GetString(content);
            return Encoding.UTF8.GetBytes(text);
        }
    }
}
