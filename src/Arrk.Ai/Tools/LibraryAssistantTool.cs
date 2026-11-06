using System.Text.Json;
using Microsoft.Extensions.AI;
using Arrk.Core.Playback;

namespace Arrk.Ai.Tools;

/// <summary>
/// The read-only library pack. It searches every module-contributed
/// <see cref="IAssistantMediaSource"/> (TV/movies, music, …) and exposes watch progress, so one
/// <c>search_media</c> tool works across domains without the assistant referencing any module.
///
/// <para>
/// Search is structured rather than a bare text box: genre, person, year, trend, library-vs-discovery
/// scope and a few deterministic sorts. Sources apply the filters they own, so "what anime do I
/// have", "the most popular action movie in my library" and "that new Tom Cruise movie" all reduce
/// to the same tool call.
/// </para>
/// </summary>
public sealed class LibraryAssistantTool(
    IEnumerable<IAssistantMediaSource> mediaSources,
    WatchStateService watchState) : IAssistantTool
{
    public string Key => "library";
    public string Name => "Library";
    public string Description => "Search movies, series and music in the library or discovery, read details, cast and watch progress.";
    public AssistantAccess Access => AssistantAccess.User;
    public IReadOnlyList<string> Keywords => [];
    public bool IsAvailable => mediaSources.Any(s => s.IsAvailable);

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string? query = null, string? domain = null, string? genre = null,
                    string? person = null, string? scope = null, string? sort = null,
                    int? yearFrom = null, int? yearTo = null, bool? anime = null,
                    int limit = 8, CancellationToken ct = default) =>
                {
                    AssistantMediaQuery request = new(
                        query?.Trim() ?? "",
                        domain,
                        limit <= 0 ? 8 : limit,
                        context.Username,
                        Genre: genre,
                        Person: person,
                        Scope: ParseScope(scope),
                        Sort: sort,
                        YearFrom: yearFrom,
                        YearTo: yearTo,
                        Anime: anime ?? DomainIsAnime(domain));

                    if (request.Text.Length == 0 && !HasFilter(request))
                        return Json(new { error = "Give a search text or at least one filter (genre, person, year, anime)." });

                    int take = Math.Clamp(request.Limit, 1, 25);
                    List<IAssistantMediaSource> sources = mediaSources
                        .Where(s => s.IsAvailable && DomainMatches(s, domain))
                        .ToList();
                    if (sources.Count == 0)
                        return Json(new { items = Array.Empty<object>(), message = "No library source matched that domain." });

                    List<Task<IReadOnlyList<AssistantMediaSummary>>> tasks = sources
                        .Select(source => SafeSearchAsync(source, request, take, ct))
                        .ToList();
                    IReadOnlyList<AssistantMediaSummary>[] results = await Task.WhenAll(tasks);

                    List<AssistantMediaSummary> merged = results
                        .SelectMany(r => r)
                        .Take(take)
                        .ToList();

                    // Follow-ups such as "download the first one" need the ids the answer refers to.
                    // The runtime keeps this bounded and re-injects it as facts on the next turn.
                    context.AddContextItems(merged);

                    List<object> items = merged.Select(ToWire).ToList();
                    return Json(new { count = items.Count, query = Describe(request), items });
                },
                "search_media",
                "Searches the user's library and discovery catalogues for movies, series, anime, albums, artists or tracks. " +
                "Filters: genre (e.g. Action, Comedy, Animation), person (actor/director name), scope (library|discovery|any), " +
                "sort (popularity|rating|newest|title|year), yearFrom/yearTo, anime (true for Japanese animation). " +
                "Use scope=library for 'what do I have' questions and scope=discovery to find something new. " +
                "Text may be empty when only filtering, e.g. genre=Action&scope=library&sort=popularity."),

            AIFunctionFactory.Create(
                async (string id, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(id))
                        return Json(new { error = "An id from search_media is required." });

                    IAssistantMediaSource? source = mediaSources
                        .Where(s => s.IsAvailable)
                        .FirstOrDefault(s => id.StartsWith(s.Key + ":", StringComparison.OrdinalIgnoreCase));
                    if (source is null)
                        return Json(new { error = "Unknown media id. Call search_media first." });

                    try
                    {
                        AssistantMediaDetail? detail = await source.GetAsync(id, context.Username, ct);
                        return detail is null
                            ? Json(new { error = "That item is no longer available." })
                            : Json(new
                            {
                                detail.Summary.Id,
                                detail.Summary.Kind,
                                detail.Summary.Title,
                                detail.Summary.Subtitle,
                                detail.Summary.Year,
                                detail.Summary.Status,
                                detail.Overview,
                                facts = detail.Facts,
                                related = detail.Related.Select(ToWire).ToList()
                            });
                    }
                    catch (Exception ex)
                    {
                        return Json(new { error = $"Could not load that item: {ex.Message}" });
                    }
                },
                "get_media",
                "Loads one movie, series, season, episode, artist, album or track by the id returned from search_media, including cast, facts and related items."),

            AIFunctionFactory.Create(
                async (int limit = 10, CancellationToken ct = default) =>
                {
                    if (!watchState.IsAvailable)
                        return Json(new { available = false, message = "No playback provider is configured, so watch progress is unavailable." });

                    try
                    {
                        IReadOnlyList<ContinueWatchingItem> items =
                            await watchState.GetContinueWatchingAsync(context.Username,
                                Math.Clamp(limit <= 0 ? 10 : limit, 1, 25), ct);

                        return Json(new
                        {
                            available = true,
                            provider = watchState.ProviderName,
                            items = items.Select(i => new
                            {
                                title = i.Title,
                                kind = i.IsEpisode ? "episode" : "movie",
                                scope = i.ScopeLabel,
                                progressPercent = (int)Math.Round(i.Progress * 100),
                                remainingMinutes = (int)Math.Round(TimeSpan.FromTicks(i.RemainingTicks).TotalMinutes),
                                lastPlayed = i.LastPlayed,
                                playUrl = i.PlayUrl
                            }).ToList()
                        });
                    }
                    catch (Exception ex)
                    {
                        return Json(new { error = $"Could not read watch progress: {ex.Message}" });
                    }
                },
                "get_continue_watching",
                "Lists what the current user has started but not finished, newest first.")
        ];
    }

    async Task<IReadOnlyList<AssistantMediaSummary>> SafeSearchAsync(
        IAssistantMediaSource source, AssistantMediaQuery query, int limit, CancellationToken ct)
    {
        try
        {
            return await source.SearchAsync(query with { Limit = limit }, ct);
        }
        catch
        {
            return [];
        }
    }

    static object ToWire(AssistantMediaSummary item) => new
    {
        id = item.Id,
        kind = item.Kind.ToString().ToLowerInvariant(),
        title = item.Title,
        subtitle = item.Subtitle,
        year = item.Year,
        status = item.Status,
        artUrl = item.ArtUrl
    };

    static AssistantMediaScope ParseScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "library" or "owned" or "local" => AssistantMediaScope.Library,
        "discovery" or "new" or "remote" or "catalogue" or "catalog" => AssistantMediaScope.Discovery,
        _ => AssistantMediaScope.Any
    };

    static bool DomainIsAnime(string? domain) =>
        !string.IsNullOrWhiteSpace(domain) && domain.Contains("anime", StringComparison.OrdinalIgnoreCase);

    static bool HasFilter(AssistantMediaQuery query) =>
        !string.IsNullOrWhiteSpace(query.Genre)
        || !string.IsNullOrWhiteSpace(query.Person)
        || query.Anime == true
        || query.YearFrom is not null
        || query.YearTo is not null;

    static string Describe(AssistantMediaQuery query)
    {
        List<string> parts = [];
        if (query.Text.Length > 0) parts.Add($"text=\"{query.Text}\"");
        if (!string.IsNullOrWhiteSpace(query.Domain)) parts.Add($"domain={query.Domain}");
        if (!string.IsNullOrWhiteSpace(query.Genre)) parts.Add($"genre={query.Genre}");
        if (!string.IsNullOrWhiteSpace(query.Person)) parts.Add($"person={query.Person}");
        if (query.Anime == true) parts.Add("anime=true");
        if (query.YearFrom is not null || query.YearTo is not null)
            parts.Add($"years={query.YearFrom?.ToString() ?? "*"}-{query.YearTo?.ToString() ?? "*"}");
        parts.Add($"scope={query.Scope.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(query.Sort)) parts.Add($"sort={query.Sort}");
        return string.Join(", ", parts);
    }

    internal static bool DomainMatches(IAssistantMediaSource source, string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return true;

        string value = domain.Trim();
        return value.ToLowerInvariant() switch
        {
            "movie" or "movies" or "film" or "films" or "tv" or "series" or "show" or "shows" or "episode" or "episodes"
                or "anime" or "animation" or "cartoon"
                => source.Key.Equals("tv", StringComparison.OrdinalIgnoreCase),
            "music" or "album" or "albums" or "artist" or "artists" or "track" or "tracks" or "song" or "songs"
                => source.Key.Equals("music", StringComparison.OrdinalIgnoreCase),
            _ => source.Key.Contains(value, StringComparison.OrdinalIgnoreCase)
                || source.DisplayName.Contains(value, StringComparison.OrdinalIgnoreCase)
        };
    }

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
