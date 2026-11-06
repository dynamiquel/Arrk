using Microsoft.Extensions.Logging;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Services;

/// <summary>Sonarr-source release provider for the interactive search picker (series/season/episode).</summary>
public class SonarrReleaseProvider(SonarrService sonarr, ILogger<SonarrReleaseProvider> logger) : ReleaseProvider
{
    public override string Name => "Shows";

    public override async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        ReleaseSearchQuery query, CancellationToken ct = default)
    {
        bool supported = query.Kind is TvReleaseKinds.Series or TvReleaseKinds.Season or TvReleaseKinds.Episode;
        if (!supported)
            return [];

        int seriesId = query.GetParentLibraryIdInt();
        int episodeId = query.Kind == TvReleaseKinds.Episode ? query.GetLibraryIdInt() : 0;
        if (seriesId <= 0 && episodeId <= 0)
            return [];

        List<ReleaseResourceDto> releases =
            await sonarr.GetReleases(
                episodeId > 0 ? episodeId : null,
                seriesId > 0 ? seriesId : null,
                query.SeasonNumber,
                ct);
        return releases.Select(r => ArrReleaseMapper.ToCandidate(r) with { Source = Name }).ToList();
    }

    public override async Task<ReleaseGrabResult> GrabAsync(
        ReleaseCandidate release, ReleaseSearchQuery query, CancellationToken ct = default)
    {
        try
        {
            await sonarr.GrabRelease(release.Guid, release.IndexerId, ct);
            return new ReleaseGrabResult(true, release, $"Grabbed \"{release.Title}\" from {release.Indexer}.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sonarr grab failed for \"{Title}\"", release.Title);
            return new ReleaseGrabResult(false, release, $"Grab failed: {ex.Message}");
        }
    }
}
