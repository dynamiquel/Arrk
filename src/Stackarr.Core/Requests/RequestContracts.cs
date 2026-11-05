
namespace Stackarr.Core.Requests;

/// <summary>
/// The granular thing a user is asking for. Distinct from <see cref="MediaDomain"/>, which is the
/// broad domain (Movie/Tv/Music). A movie request is <c>(Movie, Movie)</c>; an episode is
/// <c>(Tv, Episode)</c>; an album is <c>(Music, Album)</c>.
/// </summary>
public enum RequestKind
{
    Movie,
    Series,
    Season,
    Episode,
    Artist,
    Album,
    Track,
    Scene
}

/// <summary>Lifecycle state of a <c>MediaRequest</c>.</summary>
public enum RequestStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    Blacklisted = 3,
    Available = 4,
    Failed = 5,
    Cancelled = 6
}

/// <summary>
/// A single capability a handler can fulfil: the domain + kind pair. The registry keys on this
/// pair and enforces exactly one handler per pair.
/// </summary>
public sealed record RequestCapability(MediaDomain Domain, RequestKind Kind);

/// <summary>
/// The identity of the thing being requested. Provider ids are carried as a dictionary (tmdb,
/// tvdb, imdb, deezer, stash, ...) so the same contract serves every media backend; each
/// handler reads only the ids it understands.
/// </summary>
public sealed record RequestTarget(
    MediaDomain Domain,
    RequestKind Kind,
    IReadOnlyDictionary<string, string> ProviderIds,
    string Title,
    int? Year = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    string? PosterUrl = null)
{
    public string? GetProviderId(string key) =>
        ProviderIds.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public int GetProviderIdInt(string key) =>
        int.TryParse(GetProviderId(key), out int value) ? value : 0;

    public string ScopeLabel => Kind switch
    {
        RequestKind.Season => $"Season {SeasonNumber}",
        RequestKind.Episode => $"S{SeasonNumber}E{EpisodeNumber}",
        _ => Kind.ToString()
    };
}

/// <summary>A quality/profile option offered by a handler (arr profiles differ per backend).</summary>
public sealed record RequestQualityOption(string Key, string Name, string? Description = null);

/// <summary>The result of asking a handler whether the target is already present.</summary>
public sealed record RequestTargetState(bool Exists, bool HasFile, string? LibraryId = null, string? Message = null)
{
    public static RequestTargetState Missing() => new(false, false);
    public static RequestTargetState InLibrary(string? libraryId = null, bool hasFile = true, string? message = null) =>
        new(true, hasFile, libraryId, message);
}

/// <summary>An approved request handed to a handler to fulfil (add + monitor + search).</summary>
public sealed record FulfilmentRequest(RequestTarget Target, string? QualityKey = null, bool SearchNow = true);

/// <summary>The outcome of fulfilling a request.</summary>
public sealed record FulfilmentResult(bool Success, string? LibraryId = null, string? Message = null)
{
    public static FulfilmentResult Ok(string? libraryId = null, string? message = null) => new(true, libraryId, message);
    public static FulfilmentResult Fail(string message) => new(false, null, message);
}

/// <summary>
/// A capability a module contributes: "I can add this kind of media to a library." Implemented by
/// the library-manager modules (Tvarr, Lidarr or any alternative). The request system
/// depends only on this contract, never on a specific backend.
/// </summary>
public interface IMediaRequestHandler
{
    /// <summary>Concept key, e.g. <c>tv</c>, <c>music</c>. Matches the YAML concept.</summary>
    string Key { get; }

    /// <summary>Human-readable name, e.g. "TV &amp; Movies".</summary>
    string DisplayName { get; }

    /// <summary>The (domain, kind) pairs this handler can fulfil. Must be unique across handlers.</summary>
    IReadOnlyList<RequestCapability> Capabilities { get; }

    /// <summary>Whether the backing service is enabled/usable right now.</summary>
    bool IsAvailable { get; }

    /// <summary>Quality/profile options the user may pick for a request this handler will fulfil.</summary>
    Task<IReadOnlyList<RequestQualityOption>> GetQualityOptionsAsync(CancellationToken ct = default);

    /// <summary>Whether the target already exists in the library (used to reject duplicates).</summary>
    Task<RequestTargetState> CheckAsync(RequestTarget target, CancellationToken ct = default);

    /// <summary>Add the target to the library (and optionally search for it), applying scope.</summary>
    Task<FulfilmentResult> FulfilAsync(FulfilmentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Resolves the target to the owning module's stable media entity (used for subscriptions).
    /// Handlers that cannot name a media entity return null.
    /// </summary>
    MediaEntityRef? ResolveEntity(RequestTarget target) => null;
}

/// <summary>
/// Read-only view of the handlers contributed by every module. Built once at startup; duplicate
/// <see cref="RequestCapability"/> registrations are a programming error and throw.
/// </summary>
public interface IMediaRequestRegistry
{
    /// <summary>Every registered handler.</summary>
    IReadOnlyList<IMediaRequestHandler> Handlers { get; }

    /// <summary>Handlers whose backing service is currently available.</summary>
    IReadOnlyList<IMediaRequestHandler> AvailableHandlers { get; }

    /// <summary>Every (domain, kind) pair that has an available handler.</summary>
    IReadOnlyList<RequestCapability> AvailableCapabilities { get; }

    /// <summary>Domains with at least one available capability.</summary>
    IReadOnlyList<MediaDomain> AvailableDomains { get; }

    /// <summary>Looks up the handler for a (domain, kind) pair, regardless of availability.</summary>
    IMediaRequestHandler? GetHandler(MediaDomain domain, RequestKind kind);

    /// <summary>Whether a (domain, kind) pair has an available handler.</summary>
    bool IsAvailable(MediaDomain domain, RequestKind kind);

    /// <summary>Available kinds within a domain.</summary>
    IReadOnlyList<RequestKind> KindsFor(MediaDomain domain);
}

/// <summary>
/// Aggregates every <see cref="IMediaRequestHandler"/>. Exactly one handler may own a
/// (domain, kind) pair; a duplicate throws at startup so backend conflicts surface immediately.
/// </summary>
public sealed class MediaRequestRegistry : IMediaRequestRegistry
{
    readonly Dictionary<(MediaDomain, RequestKind), IMediaRequestHandler> _byCapability;

    public MediaRequestRegistry(IEnumerable<IMediaRequestHandler> handlers)
    {
        List<IMediaRequestHandler> all = handlers.ToList();
        Dictionary<(MediaDomain, RequestKind), IMediaRequestHandler> map = [];

        foreach (IMediaRequestHandler handler in all)
        {
            foreach (RequestCapability capability in handler.Capabilities)
            {
                if (!map.TryAdd((capability.Domain, capability.Kind), handler))
                {
                    IMediaRequestHandler existing = map[(capability.Domain, capability.Kind)];
                    throw new InvalidOperationException(
                        $"Duplicate media request capability '{capability.Domain}/{capability.Kind}' claimed by " +
                        $"'{handler.Key}' and '{existing.Key}'. Exactly one handler may own a capability.");
                }
            }
        }

        _byCapability = map;
        Handlers = all;
    }

    public IReadOnlyList<IMediaRequestHandler> Handlers { get; }

    public IReadOnlyList<IMediaRequestHandler> AvailableHandlers =>
        Handlers.Where(h => h.IsAvailable).ToList();

    public IReadOnlyList<RequestCapability> AvailableCapabilities =>
        _byCapability.Where(kv => kv.Value.IsAvailable)
            .Select(kv => new RequestCapability(kv.Key.Item1, kv.Key.Item2))
            .OrderBy(c => c.Domain).ThenBy(c => c.Kind)
            .ToList();

    public IReadOnlyList<MediaDomain> AvailableDomains =>
        AvailableCapabilities.Select(c => c.Domain).Distinct().OrderBy(d => d).ToList();

    public IMediaRequestHandler? GetHandler(MediaDomain domain, RequestKind kind) =>
        _byCapability.GetValueOrDefault((domain, kind));

    public bool IsAvailable(MediaDomain domain, RequestKind kind) =>
        _byCapability.TryGetValue((domain, kind), out IMediaRequestHandler? handler) && handler.IsAvailable;

    public IReadOnlyList<RequestKind> KindsFor(MediaDomain domain) =>
        _byCapability.Where(kv => kv.Key.Item1 == domain && kv.Value.IsAvailable)
            .Select(kv => kv.Key.Item2)
            .Distinct()
            .OrderBy(k => k)
            .ToList();
}
