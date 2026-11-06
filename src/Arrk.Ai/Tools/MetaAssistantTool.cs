using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Arrk.Ai.Tools;

/// <summary>
/// The always-on meta pack: tells the model what this Arrk instance can actually do, and gives
/// it a short usage guide. It reports configuration and media sources only, it never inspects the
/// other tool packs, so it cannot create a dependency cycle through the registry.
/// </summary>
public sealed class MetaAssistantTool(
    AiService ai,
    IEnumerable<IAssistantMediaSource> mediaSources,
    IEnumerable<IAssistantMediaActionSource> actionSources,
    IEnumerable<IAssistantRecommendationSource> recommendationSources) : IAssistantTool
{
    public string Key => "meta";
    public string Name => "Arrk";
    public string Description => "What this Arrk instance can answer and do, and how to use the assistant.";
    public AssistantAccess Access => AssistantAccess.User;

    // Only offered for capability/help questions: it saves two tool schemas on every normal turn.
    public IReadOnlyList<string> Keywords =>
    [
        "help", "what can you do", "capabilities", "commands", "how do i use", "what do you know",
        "who are you", "features"
    ];

    public bool IsAvailable => ai.Enabled;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                () => JsonSerializer.Serialize(new
                {
                    aiEnabled = ai.Enabled,
                    assistantEnabled = ai.Config.Features.Assistant.Enabled,
                    readOnly = false,
                    actionsEnabled = actionSources.Any(s => s.IsAvailable),
                    mediaSources = mediaSources
                        .Where(s => s.IsAvailable)
                        .Select(s => new { key = s.Key, name = s.DisplayName })
                        .ToList(),
                    actions = actionSources
                        .Where(s => s.IsAvailable)
                        .Select(s => new
                        {
                            key = s.Key,
                            actions = s.SupportedActions.Select(a => a.ToString().ToLowerInvariant()).ToList()
                        })
                        .ToList(),
                    recommendations = recommendationSources
                        .Where(s => s.IsAvailable)
                        .Select(s => s.Key)
                        .ToList(),
                    features = new
                    {
                        releaseRanking = ai.Config.Features.ReleaseRanking.Enabled,
                        importMatching = ai.Config.Features.ImportMatching.Enabled,
                        recommendations = ai.Config.Features.Recommendations.Enabled,
                        digest = ai.Config.Features.Digest.Enabled
                    },
                    currentUser = new { name = context.Username, admin = context.IsAdmin },
                    limits = new
                    {
                        messagesPerHour = ai.Config.Features.Assistant.MaxMessagesPerHour,
                        tokensPerUserPerDay = ai.Config.Features.Assistant.MaxTokensPerUserPerDay
                    }
                }),
                "get_capabilities",
                "Lists what this Arrk instance can answer and do: enabled features, media sources, actions, recommendation sources and the caller's role. Use it before offering capabilities."),

            AIFunctionFactory.Create(
                () => """
                    You are Arrk's assistant for a self-hosted media server. You can search the
                    library and discovery catalogues, read details/cast/watch progress, recommend
                    titles, and perform actions through tools.

                    Reading and discovery:
                    - "What anime do I have?" → search_media(domain=anime, scope=library, sort=rating)
                    - "Most trendy action movie I own" → search_media(genre=Action, scope=library, sort=popularity)
                    - "What's that new Tom Cruise movie?" → search_media(person=Tom Cruise, sort=newest, scope=discovery)
                    - "Recommend a top tier anime this season" → tv_recommend_media(domain=anime)
                    - "Surprise me with a banger" → call tv_recommend_media and music_recommend_media

                    Actions:
                    - "Request the new Spider-Man" → search_media, then tv_request_media with its id.
                    - "Add Black Panther to my watchlist" → search_media, then tv_add_to_watchlist.
                    - "Delete Re:Zero" → search_media, then tv_delete_media; the tool always returns a
                      confirmation button that the user must press.
                    - "Upgrade Scary Movie to 4K" → search_media, then tv_upgrade_quality.
                    - "Play The Last of Us" / "Play Never Forget" → search_media, then
                      tv_play_media / music_play_media.

                    For opinions and general knowledge (characters, plots, whether a show is
                    good, cast chemistry), you may answer from your own knowledge. For what the
                    user owns or has downloaded, use a tool first. Keep opinions framed as
                    opinions and do not invent library facts.

                    Only claim an action succeeded when the tool result says ok=true. Deletion and
                    quality changes are administrator-only. If the title is ambiguous, ask which one.
                    """,
                "get_help",
                "Short guide to what the assistant can answer and do, with example questions and actions.")
        ];
    }
}
