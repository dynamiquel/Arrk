using Microsoft.Extensions.Logging;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Services;

/// <summary>Radarr-source release provider for the interactive search picker (movies).</summary>
public class RadarrReleaseProvider(RadarrService radarr, ILogger<RadarrReleaseProvider> logger) : ReleaseProvider
{
    public override string Name => "Movies";

    public override async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        ReleaseSearchQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(query.Kind, TvReleaseKinds.Movie, StringComparison.OrdinalIgnoreCase))
            return [];

        int movieId = query.GetLibraryIdInt();
        if (movieId <= 0)
            return [];

        List<ReleaseResourceDto> releases = await radarr.GetReleases(movieId, ct);
        return releases.Select(r => ArrReleaseMapper.ToCandidate(r) with { Source = Name }).ToList();
    }

    public override async Task<ReleaseGrabResult> GrabAsync(
        ReleaseCandidate release, ReleaseSearchQuery query, CancellationToken ct = default)
    {
        try
        {
            await radarr.GrabRelease(release.Guid, release.IndexerId, ct);
            return new ReleaseGrabResult(true, release, $"Grabbed \"{release.Title}\" from {release.Indexer}.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Radarr grab failed for \"{Title}\"", release.Title);
            return new ReleaseGrabResult(false, release, $"Grab failed: {ex.Message}");
        }
    }
}
