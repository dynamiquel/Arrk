using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Arrk.Ai;

/// <summary>
/// Configuration for the optional AI module. AI is <b>off by default</b> and every AI-backed
/// feature must treat it as an advisor, never a source of truth: when it is disabled (or a request
/// fails) callers fall back to their deterministic behaviour.
///
/// The transport is provider-agnostic: everything is driven through the
/// <c>Microsoft.Extensions.AI</c> <c>IChatClient</c> abstraction, and the built-in providers are all
/// OpenAI-compatible endpoints, so a user can point Arrk at OpenAI, Google Gemini, a local Ollama
/// (or LM Studio/vLLM) or any custom gateway without a code change.
/// </summary>
public class AiConfig
{
    public const string SectionName = "ai";

    [Description("Enable AI-assisted features. Off by default; when off no AI requests are ever made.")]
    public bool Enabled { get; set; }

    [Description("Provider preset. Every preset is an OpenAI-compatible endpoint, so this only picks a default base URL, set baseUrl to override or use 'custom'. Prefer a preset like openrouter or custom for Claude, which Anthropic serves through its OpenAI-compatibility layer.")]
    [Display(Name = "Provider")]
    [AllowedValues(AiProviders.OpenAi, AiProviders.Anthropic, AiProviders.Gemini, AiProviders.Groq,
        AiProviders.DeepSeek, AiProviders.XAi, AiProviders.Mistral, AiProviders.OpenRouter,
        AiProviders.Together, AiProviders.Perplexity, AiProviders.Ollama, AiProviders.LmStudio,
        AiProviders.Custom)]
    public string Provider { get; set; } = AiProviders.OpenAi;

    public AiModelsConfig Models { get; set; } = new();

    [Description("API key for the provider. Not required for a local Ollama/LM Studio; leave blank if the endpoint needs no auth.")]
    [Display(Name = "API key")]
    [DataType(DataType.Password)]
    public string ApiKey { get; set; } = "";

    [Description("Endpoint override. Always wins over the provider preset's default; required for 'custom'. Any OpenAI-compatible URL works.")]
    [Display(Name = "Base URL")]
    public string BaseUrl { get; set; } = "";

    [Description("Request timeout in seconds.")]
    [Display(Name = "Timeout (seconds)")]
    public int TimeoutSeconds { get; set; } = 60;

    [Description("Optional system prompt prepended to every AI request. Leave blank for none.")]
    [Display(Name = "System prompt")]
    public string SystemPrompt { get; set; } = "";

    public AiFeaturesConfig Features { get; set; } = new();

    public AiLimitsConfig Limits { get; set; } = new();

    public AiPrivacyConfig Privacy { get; set; } = new();

    public AiPricingConfig Pricing { get; set; } = new();

    public AiPersonasConfig Personas { get; set; } = new();

    /// <summary>The model used for conversation.</summary>
    public string ResolveChatModel() => Models.Chat.Trim();

    /// <summary>The cheaper model used for ranking/parsing. Falls back to the chat model.</summary>
    public string ResolveFastModel() =>
        !string.IsNullOrWhiteSpace(Models.Fast) ? Models.Fast.Trim() : ResolveChatModel();
}

/// <summary>The two model profiles: conversation and cheap background reasoning.</summary>
public class AiModelsConfig
{
    [Description("Model used for the assistant and anything user-facing. Required when AI is enabled.")]
    [Display(Name = "Chat model")]
    public string Chat { get; set; } = "";

    [Description("Cheaper model for ranking, parsing and summaries. Falls back to the chat model when blank.")]
    [Display(Name = "Fast model")]
    public string Fast { get; set; } = "";
}

/// <summary>Per-feature opt-ins. Every automatic AI behaviour has its own toggle.</summary>
public class AiFeaturesConfig
{
    public AiAssistantFeature Assistant { get; set; } = new();

    public AiReleaseRankingFeature ReleaseRanking { get; set; } = new();

    public AiImportMatchingFeature ImportMatching { get; set; } = new();

    public AiRecommendationsFeature Recommendations { get; set; } = new();

    public AiDigestFeature Digest { get; set; } = new();

    public AiStyleFeature Style { get; set; } = new();
}

public class AiStyleFeature
{
    [Description("Let the optional AI helper generate per-account custom CSS from a plain-language brief. The user reviews and saves the result.")]
    public bool Enabled { get; set; } = true;
}

public class AiAssistantFeature
{
    [Description("Enable the in-app assistant. Chat is on-demand, so it only spends tokens when a user asks.")]
    public bool Enabled { get; set; } = true;

    [Description("Maximum assistant messages a user may send per hour (0 = unlimited).")]
    public int MaxMessagesPerHour { get; set; } = 30;

    [Description("Maximum tokens one user may spend on the assistant per day (0 = unlimited).")]
    public long MaxTokensPerUserPerDay { get; set; } = 60_000;

    [Description("Tool rounds allowed per question (1–4). Lower costs fewer tokens; 2 is enough for a search plus an action.")]
    public int MaxToolRounds { get; set; } = 2;
}

public class AiReleaseRankingFeature
{
    [Description("Let AI break ties between already-eligible releases. The deterministic scoring stays the primary path.")]
    public bool Enabled { get; set; } = true;

    [Description("off = never call; shadow = record what it would pick without changing behaviour; ambiguous = only when the top candidates are tied or edition markers are present; always = pick among the top candidates.")]
    [AllowedValues(AiRankingModes.Off, AiRankingModes.Shadow, AiRankingModes.Ambiguous, AiRankingModes.Always)]
    public string Mode { get; set; } = AiRankingModes.Shadow;

    [Description("Maximum candidates sent to the model.")]
    public int MaxCandidates { get; set; } = 12;

    [Description("Minimum confidence (0–1) required to override the deterministic pick.")]
    public double MinConfidence { get; set; } = 0.6;

    [Description("How close the top two deterministic scores must be for 'ambiguous' mode to call the model.")]
    public int AmbiguityScoreWindow { get; set; } = 5;
}

/// <summary>
/// Optional AI help when a locally uploaded file is ambiguous. The deterministic matcher always
/// runs first; the model may only choose among the candidates it already produced.
/// </summary>
public class AiImportMatchingFeature
{
    [Description("Let AI break ties when a local upload could match more than one movie or show. The deterministic parser and matcher stay the primary path.")]
    public bool Enabled { get; set; } = true;

    [Description("off = never call; shadow = record what it would pick without changing the match; ambiguous = only when the top candidates are close; always = pick among the top candidates.")]
    [AllowedValues(AiRankingModes.Off, AiRankingModes.Shadow, AiRankingModes.Ambiguous, AiRankingModes.Always)]
    public string Mode { get; set; } = AiRankingModes.Ambiguous;

    [Description("Maximum candidates sent to the model.")]
    public int MaxCandidates { get; set; } = 8;

    [Description("Minimum confidence (0–1) required to override the deterministic match.")]
    public double MinConfidence { get; set; } = 0.6;

    [Description("How close the top two match scores must be for 'ambiguous' mode to call the model.")]
    public int AmbiguityScoreWindow { get; set; } = 8;
}

public class AiRecommendationsFeature
{
    [Description("Let AI re-rank the deterministic per-user recommendation list and write reasons. The list is still produced with AI off.")]
    public bool Enabled { get; set; } = true;

    [Description("How many picks to show per user and domain.")]
    public int PicksPerUser { get; set; } = 12;

    [Description("How often the cached per-user list is rebuilt.")]
    public int RefreshHours { get; set; } = 24;
}

public class AiDigestFeature
{
    [Description("Send a scheduled summary of downloads, requests, lifecycle, storage and speedtests.")]
    public bool Enabled { get; set; } = true;

    [Description("Cron expression for the digest job.")]
    public string Schedule { get; set; } = "0 8 * * *";
}

public class AiLimitsConfig
{
    [Description("Maximum AI requests per day across all features (0 = unlimited).")]
    public int MaxRequestsPerDay { get; set; } = 500;

    [Description("Maximum tokens per month across all features (0 = unlimited).")]
    public long MaxTokensPerMonth { get; set; } = 2_000_000;

    [Description("How long a cached AI response stays valid.")]
    public int CacheMinutes { get; set; } = 60;

    [Description("How many days of AI usage records to keep (0 = keep forever).")]
    public int UsageRetentionDays { get; set; } = 90;
}

public class AiPrivacyConfig
{
    [Description("Store prompt and response text on the usage record. Off by default; token counts and feature attribution are always stored.")]
    public bool StorePromptText { get; set; }
}

/// <summary>
/// Persona selection. Built-ins are always available; custom entries are appended by key. Users
/// pick a persona in the assistant popup and the choice is stored per user.
/// </summary>
public class AiPersonasConfig
{
    [Description("Default persona key for users who have not chosen one. Built-ins: plain, jarvis, cortana, johnson, emilia, coconut.")]
    public string Default { get; set; } = AiPersonas.Plain;

    [Description("Additional personas. The key must be unique and the style is a tone instruction, not a replacement for the assistant's fact rules. The shipped defaults include Johnson, Emilia and Coconut; edit or remove them freely.")]
    public List<AiPersonaDefinition> Custom { get; set; } =
    [
        new()
        {
            Key = "johnson",
            Name = "Sergeant Johnson",
            Description = "Boisterous, battle-hardened Marine sergeant.",
            Style = "Speak like Sergeant Major Avery Johnson: boisterous, battle-hardened and blunt, with dry barbs and Marine cadence. Use the occasional 'oorah' or 'son' without laying it on thick, and keep the respect."
        },
        new()
        {
            Key = "emilia",
            Name = "Emilia",
            Description = "Gentle, earnest and politely formal.",
            Style = "Speak like Emilia: gentle, earnest and politely formal, with a warm but slightly sheltered sincerity. Be encouraging and fair, gently push back if treated like a child, and avoid modern slang."
        },
        new()
        {
            Key = "coconut",
            Name = "Coconut",
            Description = "Cheerful, affectionate and a little clumsy.",
            Style = "Speak like Coconut: cheerful, affectionate and a little clumsy, with playful cat-like phrasing ('nya' sparingly). Be flirtatious and teasing when invited, but keep it light and tasteful; never explicit."
        }
    ];
}

public class AiPersonaDefinition
{
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    [Description("Tone/style instruction for the model. It cannot change library facts, permissions or action confirmation.")]
    public string Style { get; set; } = "";
}

public class AiPricingConfig
{
    [Description("Optional input price per million tokens, used only to estimate cost on the admin page.")]
    public decimal InputPerMillion { get; set; }

    [Description("Optional output price per million tokens, used only to estimate cost on the admin page.")]
    public decimal OutputPerMillion { get; set; }
}

public static class AiRankingModes
{
    public const string Off = "off";
    public const string Shadow = "shadow";
    public const string Ambiguous = "ambiguous";
    public const string Always = "always";

    public static readonly string[] All = [Off, Shadow, Ambiguous, Always];

    public static bool IsKnown(string? mode) =>
        !string.IsNullOrWhiteSpace(mode)
        && All.Contains(mode, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Stable feature keys recorded on usage rows and shown on the admin page.</summary>
public static class AiFeatureKeys
{
    public const string Assistant = "assistant";
    public const string ReleaseRanking = "releaseRanking";
    public const string ImportMatching = "importMatching";
    public const string Recommendations = "recommendations";
    public const string Digest = "digest";
    public const string Style = "style";
    public const string Core = "core";
}
