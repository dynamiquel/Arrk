using System.Text.Json;
using Microsoft.Extensions.AI;
using Arrk.Ai;

namespace Arrk.Tvarr.Tools;

/// <summary>
/// TV/movie recommendation pack, owned by the TV module. The catalogue source generates the picks
/// and their factual reasons; this tool only shapes the response for the model.
/// </summary>
public sealed class TvarrAssistantRecommendationsTool(
    IAssistantRecommendationSource source) : IAssistantTool
{
    public string Key => "tv-recommend";
    public string Name => "Movies & TV recommendations";
    public string Description => "Suggest films, series and anime the user does not already have.";
    public AssistantAccess Access => AssistantAccess.User;

    public IReadOnlyList<string> Keywords =>
    [
        "recommend", "suggest", "surprise", "banger", "something to watch", "what should i watch",
        "top tier", "similar to", "in the mood", "pick me", "give me something", "anime this season"
    ];

    public bool IsAvailable => source.IsAvailable;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string? domain = null, string? genre = null, bool? anime = null,
                    int limit = 6, CancellationToken ct = default) =>
                {
                    int take = Math.Clamp(limit <= 0 ? 6 : limit, 1, 15);
                    string? scope = NormalizeDomain(domain);
                    if (scope is null)
                        return Json(new { items = Array.Empty<object>(), message = "That domain is not movies or TV." });

                    AssistantRecommendationQuery query = new(
                        scope, genre, take, context.Username, anime ?? scope.Contains("anime", StringComparison.OrdinalIgnoreCase));

                    IReadOnlyList<AssistantRecommendation> picks = await SafeAsync(source, query, ct);
                    List<object> items = picks
                        .OrderByDescending(p => p.Score)
                        .Take(take)
                        .Select(p => (object)new
                        {
                            id = p.Item.Id,
                            kind = p.Item.Kind.ToString().ToLowerInvariant(),
                            title = p.Item.Title,
                            subtitle = p.Item.Subtitle,
                            year = p.Item.Year,
                            status = p.Item.Status,
                            reason = p.Reason
                        })
                        .ToList();

                    return Json(new { count = items.Count, items });
                },
                "tv_recommend_media",
                "Recommends a movie, series or anime the user does not already have. Filter by genre and anime=true. " +
                "Every pick has a factual reason (rating, trending, current season). Use for 'surprise me' or " +
                "'recommend an anime this season'.")
        ];
    }

    static async Task<IReadOnlyList<AssistantRecommendation>> SafeAsync(
        IAssistantRecommendationSource source, AssistantRecommendationQuery query, CancellationToken ct)
    {
        try
        {
            return await source.RecommendAsync(query, ct);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Returns null when the request clearly belongs to another domain.</summary>
    static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        string value = domain.Trim().ToLowerInvariant();
        return value switch
        {
            "movie" or "movies" or "film" or "films" or "tv" or "series" or "show" or "shows"
                or "anime" or "animation" => value,
            _ => null
        };
    }

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
