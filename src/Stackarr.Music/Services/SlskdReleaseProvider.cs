using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Slskd;

namespace Stackarr.Music.Services;

/// <summary>
/// Soulseek (slskd) source for the shared interactive-search picker. Groups each peer's matching
/// files into a candidate (a folder for an album, a file for a track) so Soulseek results sit
/// alongside the torrent results, giving a real fallback when indexers have nothing.
/// </summary>
public class SlskdReleaseProvider(
    SlskdService slskd,
    ILogger<SlskdReleaseProvider> logger,
    MusicAcquisitionService? acquisition = null) : ReleaseProvider
{
    public override string Name => "Soulseek";

    public override async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        ReleaseSearchQuery query, CancellationToken ct = default)
    {
        List<ReleaseCandidate> results = [];
        await foreach (ReleaseCandidate release in SearchStreamAsync(query, ct))
            results.Add(release);
        return results;
    }

    public override async IAsyncEnumerable<ReleaseCandidate> SearchStreamAsync(
        ReleaseSearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string title = !string.IsNullOrWhiteSpace(query.Album) ? query.Album : query.Title;
        if (string.IsNullOrWhiteSpace(title))
            yield break;
        if (!slskd.IsEnabled || !await slskd.IsAvailableAsync(ct))
            yield break;

        // Title-first (Soulseek users name folders by album), then fall back to artist+title.
        SlskdSearch? search = null;
        try
        {
            search = await slskd.SearchAsync(title, timeoutSeconds: 15, ct);
            if (search is null || search.Responses.Count == 0)
            {
                string withArtist = string.IsNullOrWhiteSpace(query.Artist) ? title : $"{query.Artist} {title}";
                SlskdSearch? second = await slskd.SearchAsync(withArtist, timeoutSeconds: 15, ct);
                if (second is { Responses.Count: > 0 })
                    search = second;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Soulseek search failed for {Title}", title);
            yield break;
        }
        if (search is null)
            yield break;
        logger.LogDebug("Soulseek search for \"{Title}\" returned {Peers} peer(s), {Files} file(s)",
            title, search.Responses.Count, search.Responses.Sum(r => r.Files.Count));

        HashSet<string> titleTokens = Tokens(title);
        string normTitle = Normalize(title);
        bool albumRequest = !string.Equals(query.Kind, MusicReleaseKinds.Track, StringComparison.OrdinalIgnoreCase);

        // Group files per (peer, folder) so an album is one row and a loose track is another.
        List<Candidate> candidates = [];
        Dictionary<string, Candidate> groups = new(StringComparer.OrdinalIgnoreCase);
        foreach (SlskdResponse response in search.Responses)
        {
            foreach (SlskdFile file in response.Files)
            {
                string path = file.Filename.Replace('\\', '/');
                if (!IsAudio(path))
                    continue;

                string folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                string key = albumRequest ? $"{response.Username}\u0001{folder}" : $"{response.Username}\u0001{path}";
                if (!groups.TryGetValue(key, out Candidate? candidate))
                {
                    candidate = new Candidate
                    {
                        Peer = response.Username,
                        Folder = folder,
                        HasFreeSlot = response.HasFreeUploadSlot
                    };
                    groups[key] = candidate;
                }
                candidate.Files.Add(file);
                candidate.HasFreeSlot |= response.HasFreeUploadSlot;
            }
        }

        foreach (Candidate candidate in groups.Values)
        {
            if (!IsRelevant(candidate.Files, titleTokens, normTitle, query.Artist))
                continue;

            long size = candidate.Files.Sum(f => f.Size);
            bool lossless = candidate.Files.Any(f => LooksLossless(f.Filename)) || candidate.Files.Any(f => (f.BitRate ?? 0) >= 1000);
            int bitrate = candidate.Files.Max(f => f.BitRate ?? 0);
            string display = albumRequest
                ? (string.IsNullOrWhiteSpace(candidate.Folder) ? Path.GetFileName(candidate.Files[0].Filename) : Path.GetFileName(candidate.Folder.Replace('\\', '/')))
                : Path.GetFileName(candidate.Files[0].Filename.Replace('\\', '/'));

            // Drop mono transfers too (see MusicAcquisitionService.IsMonoRelease): the rule is shared
            // with the torrent scorer so both halfs of the picker agree.
            if (MusicAcquisitionService.IsMonoRelease(display, title, query.Artist))
                continue;

            int score = (lossless ? 1000 : bitrate) + (candidate.HasFreeSlot ? 25 : 0) + Math.Min(candidate.Files.Count, 40);

            candidates.Add(new Candidate
            {
                Peer = candidate.Peer,
                Folder = candidate.Folder,
                HasFreeSlot = candidate.HasFreeSlot,
                Files = candidate.Files,
                Display = display,
                Size = size,
                Lossless = lossless,
                Score = score
            });
        }

        foreach (Candidate candidate in candidates.OrderByDescending(c => c.Score).Take(40))
        {
            ct.ThrowIfCancellationRequested();
            yield return new ReleaseCandidate
            {
                Id = $"slskd:{candidate.Peer}:{candidate.Folder}",
                Title = string.IsNullOrWhiteSpace(candidate.Display) ? $"{candidate.Files.Count} file(s)" : candidate.Display,
                Indexer = $"Soulseek · {candidate.Peer}",
                Source = Name,
                Protocol = "soulseek",
                Size = candidate.Size,
                Seeders = candidate.Files.Count,
                Score = candidate.Score,
                QualityLabel = candidate.Lossless ? "Lossless" : "Lossy",
                IsLossless = candidate.Lossless,
                SourceFiles = candidate.Files
                    .Select(f => new ReleaseSourceFile(candidate.Peer, f.Filename, f.Size))
                    .ToList()
            };
        }
    }

    public override async Task<ReleaseGrabResult> GrabAsync(
        ReleaseCandidate release, ReleaseSearchQuery query, CancellationToken ct = default)
    {
        try
        {
            if (release.SourceFiles.Count == 0)
                return new ReleaseGrabResult(false, release, "This Soulseek result has no files to download.");

            string peer = release.SourceFiles[0].Peer;
            List<SlskdFile> files = release.SourceFiles
                .Select(f => new SlskdFile { Filename = f.Filename, Size = f.Size })
                .ToList();

            // Record the grab through the acquisition service so the completed download is placed into
            // the album it was for (and a failed source can be blacklisted), not as a bare folder.
            if (acquisition is not null)
            {
                string artist = query.Artist ?? "";
                string title = !string.IsNullOrWhiteSpace(query.Album)
                    ? query.Album
                    : (string.IsNullOrWhiteSpace(query.Title) ? release.Title : query.Title);
                MusicGrabResult result = await acquisition.GrabSoulseekAsync(
                    artist, title, query.GetLibraryIdInt(), peer, files, release.Title, ct);
                return new ReleaseGrabResult(result.Success, release, result.Message);
            }

            bool queued = await slskd.EnqueueAsync(peer, files, ct);
            return queued
                ? new ReleaseGrabResult(true, release, $"Queued {files.Count} file(s) from Soulseek peer {peer}.")
                : new ReleaseGrabResult(false, release, "slskd refused the download.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Soulseek grab failed for \"{Title}\"", release.Title);
            return new ReleaseGrabResult(false, release, $"Grab failed: {ex.Message}");
        }
    }

    static bool IsRelevant(List<SlskdFile> files, HashSet<string> titleTokens, string normTitle, string artist)
    {
        List<string> paths = files.Select(f => f.Filename.Replace('\\', '/')).ToList();
        bool nameHit = paths.Any(p =>
            Normalize(Path.GetFileName(p)).Contains(normTitle, StringComparison.Ordinal) ||
            Normalize(Path.GetDirectoryName(p) ?? "").Contains(normTitle, StringComparison.Ordinal));
        if (nameHit)
            return true;

        HashSet<string> all = files
            .SelectMany(f => Tokens(f.Filename))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (titleTokens.Count > 0)
        {
            int need = Math.Max(1, (int)Math.Ceiling(titleTokens.Count * 0.5));
            if (titleTokens.Count(all.Contains) >= need)
                return true;
        }

        // Last resort: keep it if the artist's distinctive tokens appear (user searched a real song).
        HashSet<string> artistTokens = [.. Tokens(artist).Where(t => t.Length >= 3)];
        return artistTokens.Count > 0 && artistTokens.Overlaps(all);
    }

    static bool LooksLossless(string name)
    {
        string n = name.ToLowerInvariant();
        return n.EndsWith(".flac") || n.EndsWith(".wav") || n.EndsWith(".alac") || n.EndsWith(".ape") || n.Contains("flac");
    }

    static bool IsAudio(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    static readonly string[] AudioExtensions =
        [".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".alac", ".wma", ".ape", ".mpc", ".dsf", ".dff"];

    static HashSet<string> Tokens(string value) =>
        value.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static readonly char[] WordSeparators =
        [' ', '-', '_', '.', ',', ':', ';', '(', ')', '[', ']', '{', '}', '\'', '’', '`',
         '&', '/', '\\', '+', '!', '?', '"', '~', '*', '|', '<', '>', '=', '\t'];

    sealed class Candidate
    {
        public string Peer { get; init; } = "";
        public string Folder { get; init; } = "";
        public bool HasFreeSlot { get; set; }
        public List<SlskdFile> Files { get; init; } = [];
        public string Display { get; init; } = "";
        public long Size { get; init; }
        public bool Lossless { get; init; }
        public int Score { get; init; }
    }
}
