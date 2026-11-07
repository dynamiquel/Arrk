using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Arrk.Ai.Tools;

/// <summary>
/// The on-demand meta pack: tells the model what this Arrk instance can actually do, and points
/// to the searchable product guides. It reports configuration and media sources only, it never inspects the
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
                    Check get_capabilities before promising instance-specific features or actions.
                    For setup and usage, call search_documentation with keywords (or empty to browse
                    topics), then read_documentation for the relevant section. Module and plugin docs
                    cover installation, video/uploads/quality, music, downloads/VPN, playback/subtitles,
                    household/requests/remote access, history/cleanup, notifications and AI.
                    They are documentation, not live state or permission to change settings.
                    For holdings, availability and downloads use live tools. Resolve ambiguous titles
                    before acting; only report success when the action tool confirms it. Deletion and
                    quality changes require administrator access and explicit confirmation. Label
                    general opinions as opinions. Detailed assistant examples are in the assistant guide.
                    """,
                "get_help",
                "How to find product instructions and distinguish documentation from live tools and actions.")
        ];
    }
}
