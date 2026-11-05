namespace Stackarr.Ai;

/// <summary>The media shape a source can return, used for display and for routing a detail lookup.</summary>
public enum AssistantMediaKind
{
    Movie,
    Series,
    Season,
    Episode,
    Artist,
    Album,
    Track
}

/// <summary>Where a search should look: the user's library, discovery, or both.</summary>
public enum AssistantMediaScope
{
    /// <summary>Library rows first, then discovery results (the default).</summary>
    Any,

    /// <summary>Only items the user already has in the library.</summary>
    Library,

    /// <summary>Only items that are not in the library, so answers are new suggestions.</summary>
    Discovery
}

/// <summary>
/// A compact, model-safe description of one media item. Sources never hand raw arr/provider DTOs to
/// the assistant, so the model only sees fields a user could see in the UI.
/// </summary>
public sealed record AssistantMediaSummary(
    string Domain,
    string Id,
    AssistantMediaKind Kind,
    string Title,
    string Subtitle = "",
    int? Year = null,
    string Status = "",
    string? ArtUrl = null);

/// <summary>One media item with the facts the assistant may quote, plus drill-down relations.</summary>
public sealed record AssistantMediaDetail(
    AssistantMediaSummary Summary,
    string Overview,
    IReadOnlyList<string> Facts,
    IReadOnlyList<AssistantMediaSummary> Related);

/// <summary>
/// A library or discovery search from the assistant. Text may be empty when the user is browsing by
/// a structured filter (genre, person, year, trend). Every source applies the filters it owns and
/// ignores the ones it has no concept of.
/// </summary>
public sealed record AssistantMediaQuery(
    string Text,
    string? Domain,
    int Limit,
    string? Username,
    string? Genre = null,
    string? Person = null,
    AssistantMediaScope Scope = AssistantMediaScope.Any,
    string? Sort = null,
    int? YearFrom = null,
    int? YearTo = null,
    bool? Anime = null);

/// <summary>
/// One library domain exposed to the assistant (TV/movies, music, …). Implemented by the module that
/// owns the data so the assistant's library pack can search and drill into any domain uniformly.
/// Ids are opaque to the runtime and always owned by the source that produced them.
/// </summary>
public interface IAssistantMediaSource
{
    /// <summary>Short key used as the id prefix, e.g. <c>tv</c> or <c>music</c>.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>Whether the owning service is configured enough to answer.</summary>
    bool IsAvailable { get; }

    Task<IReadOnlyList<AssistantMediaSummary>> SearchAsync(AssistantMediaQuery query, CancellationToken ct);

    /// <summary>
    /// Full detail for an id returned by <see cref="SearchAsync"/> or by another detail's
    /// <see cref="AssistantMediaDetail.Related"/> list. Returns <c>null</c> when the id is unknown.
    /// </summary>
    Task<AssistantMediaDetail?> GetAsync(string id, string? username, CancellationToken ct);
}

/// <summary>
/// One suggestion with the deterministic reason it was picked. Reasons are built by code from real
/// metadata (rating, chart position, library gap), never invented by a model.
/// </summary>
public sealed record AssistantRecommendation(AssistantMediaSummary Item, string Reason, double Score = 0);

/// <summary>A recommendation request: bounded domain/genre, caller and size.</summary>
public sealed record AssistantRecommendationQuery(
    string? Domain,
    string? Genre,
    int Limit,
    string? Username,
    bool? Anime = null);

/// <summary>
/// Candidate generation for the assistant's recommendation tool. Implemented by a module that owns
/// a catalogue; the assistant only merges and ranks the returned candidates deterministically.
/// </summary>
public interface IAssistantRecommendationSource
{
    string Key { get; }

    bool IsAvailable { get; }

    Task<IReadOnlyList<AssistantRecommendation>> RecommendAsync(
        AssistantRecommendationQuery query, CancellationToken ct);
}
