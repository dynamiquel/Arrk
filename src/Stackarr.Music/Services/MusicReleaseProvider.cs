using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Music.Services;

/// <summary>
/// Music release provider for the shared interactive-search picker. Wraps the Stackarr-native
/// acquisition service (title-first Prowlarr search, scored, grabbed via Transmission) and maps its
/// <see cref="MusicRelease"/> rows to the shared candidate shape. Soulseek results are contributed
/// separately by <see cref="SlskdReleaseProvider"/>.
/// </summary>
public class MusicReleaseProvider(MusicCatalogService catalog, ILogger<MusicReleaseProvider> logger) : ReleaseProvider
{
    public override string Name => "Prowlarr";

    public override async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        ReleaseSearchQuery query, CancellationToken ct = default)
    {
        List<ReleaseCandidate> results = [];
        await foreach (ReleaseCandidate release in SearchStreamAsync(query, ct))
            results.Add(release);
        return results;
    }

    /// <summary>Streams Prowlarr candidates as each query variant returns.</summary>
    public override async IAsyncEnumerable<ReleaseCandidate> SearchStreamAsync(
        ReleaseSearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string title = !string.IsNullOrWhiteSpace(query.Album) ? query.Album : query.Title;
        if (string.IsNullOrWhiteSpace(title))
            yield break;

        await foreach (MusicRelease release in catalog.StreamReleasesAsync(query.Artist, title, ct).WithCancellation(ct))
            yield return ToCandidate(release);
    }

    public override async Task<ReleaseGrabResult> GrabAsync(
        ReleaseCandidate release, ReleaseSearchQuery query, CancellationToken ct = default)
    {
        try
        {
            MusicRelease music = new()
            {
                Title = release.Title,
                Indexer = release.Indexer,
                Size = release.Size,
                Seeders = release.Seeders,
                MagnetUrl = release.MagnetUrl,
                DownloadUrl = release.DownloadUrl,
                InfoHash = release.InfoHash,
                Protocol = release.Protocol,
                PublishDate = release.PublishDate,
                IsLossless = release.IsLossless,
                HasMagnet = !string.IsNullOrEmpty(release.MagnetUrl),
                Score = release.Score
            };
            MusicGrabResult result = await catalog.GrabReleaseAsync(music, query.GetLibraryIdInt(), ct);
            return new ReleaseGrabResult(result.Success, release, result.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Music grab failed for \"{Title}\"", release.Title);
            return new ReleaseGrabResult(false, release, $"Grab failed: {ex.Message}");
        }
    }

    /// <summary>Maps a scored music release to the shared candidate shape (public for testing).</summary>
    public static ReleaseCandidate ToCandidate(MusicRelease r) => new()
    {
        Id = !string.IsNullOrEmpty(r.InfoHash) ? r.InfoHash : r.Title,
        Title = r.Title,
        Indexer = r.Indexer,
        Source = "Prowlarr",
        Protocol = r.Protocol,
        Size = r.Size,
        Seeders = r.Seeders,
        PublishDate = r.PublishDate,
        Score = r.Score,
        QualityLabel = r.IsLossless ? "Lossless" : "Lossy",
        IsLossless = r.IsLossless,
        MagnetUrl = r.MagnetUrl,
        DownloadUrl = r.DownloadUrl,
        InfoHash = r.InfoHash
    };
}
