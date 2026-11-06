using System.Text.Json;
using Microsoft.Extensions.AI;
using Arrk.Ai;

namespace Arrk.Music.Tools;

/// <summary>
/// Music recommendation pack, owned by the music module. Deezer charts, genre charts and related
/// artists provide the candidates and their factual reasons; this tool only shapes the response.
/// </summary>
public sealed class MusicAssistantRecommendationsTool(
    IAssistantRecommendationSource source) : IAssistantTool
{
    public string Key => "music-recommend";
    public string Name => "Music recommendations";
    public string Description => "Suggest albums and artists the user does not already have.";
    public AssistantAccess Access => AssistantAccess.User;

    public IReadOnlyList<string> Keywords =>
    [
        "recommend", "suggest", "surprise", "banger", "something to listen", "what should i listen",
        "similar to", "in the mood", "pick me", "give me something", "album", "artist", "song", "track"
    ];

    public bool IsAvailable => source.IsAvailable;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string? genre = null, int limit = 6, CancellationToken ct = default) =>
                {
                    int take = Math.Clamp(limit <= 0 ? 6 : limit, 1, 15);
                    AssistantRecommendationQuery query = new("music", genre, take, context.Username);
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
                            reason = p.Reason,
                            artUrl = p.Item.ArtUrl
                        })
                        .ToList();

                    return Json(new { count = items.Count, items });
                },
                "music_recommend_media",
                "Recommends an album or artist the user does not already have, optionally in a genre. " +
                "Every pick has a factual reason (charting, related to an artist they own).")
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

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
