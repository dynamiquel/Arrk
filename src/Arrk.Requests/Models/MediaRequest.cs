using Arrk.Core.Enums;
using Arrk.Core.Requests;

namespace Arrk.Requests.Models;

/// <summary>
/// A persisted request for media raised by an Arrk account. Stores enough identity
/// (domain/kind/provider ids/handler key/quality) that it renders meaningfully in history even
/// after the fulfilling backend is disabled or replaced.
/// </summary>
public class MediaRequest
{
    public long Id { get; set; }

    public int RequestedByUserId { get; set; }

    /// <summary>Denormalised so history survives account renames/deletions.</summary>
    public string RequestedByUsername { get; set; } = "";

    public DateTime RequestedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MediaDomain Domain { get; set; }
    public RequestKind Kind { get; set; }

    /// <summary>JSON dictionary of provider ids (tmdb/tvdb/imdb/deezer/stash...).</summary>
    public string ProviderIds { get; set; } = "{}";

    public string Title { get; set; } = "";
    public int? Year { get; set; }

    /// <summary>Poster snapshot so the request card renders even when the catalog is unavailable.</summary>
    public string? PosterUrl { get; set; }

    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    /// <summary>Concept key of the handler intended to fulfil this request (e.g. "tv").</summary>
    public string HandlerKey { get; set; } = "";

    public string? QualityKey { get; set; }
    public string? QualityName { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Pending;

    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }

    /// <summary>Library id assigned by the fulfilling backend once approved (Radarr/Sonarr id).</summary>
    public string? LibraryId { get; set; }

    /// <summary>Media service key of the requested entity, e.g. <c>tv</c>.</summary>
    public string? ProviderKey { get; set; }

    /// <summary>Stable media entity key used to match imports and notifications.</summary>
    public string? EntityKey { get; set; }

    public DateTime? NotifiedAt { get; set; }

    public RequestTarget ToTarget() => new(
        Domain,
        Kind,
        ProviderIdMap.Deserialize(ProviderIds),
        Title,
        Year,
        SeasonNumber,
        EpisodeNumber);

    public string ScopeLabel => Kind switch
    {
        RequestKind.Season => $"Season {SeasonNumber}",
        RequestKind.Episode => $"S{SeasonNumber}E{EpisodeNumber}",
        _ => Kind.ToString()
    };
}
