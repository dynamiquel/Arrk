namespace Stackarr.Ai;

/// <summary>
/// The provider values accepted by <see cref="AiConfig.Provider"/>. Every provider speaks the
/// OpenAI-compatible protocol, so this is not a list of protocols, it is a small directory of
/// <b>default endpoints</b>. The endpoint is the real configuration (see
/// <see cref="AiConfig.BaseUrl"/>); a preset merely saves the user from looking the URL up, and
/// <see cref="Custom"/> lets any other OpenAI-compatible host (or a new provider) be used without a
/// code change.
/// </summary>
public static class AiProviders
{
    public const string OpenAi = "openai";

    /// <summary>Anthropic Claude via its OpenAI-compatibility layer (base URL <c>https://api.anthropic.com/v1/</c>).</summary>
    public const string Anthropic = "anthropic";

    /// <summary>Google Gemini via its OpenAI-compatibility endpoint.</summary>
    public const string Gemini = "gemini";

    public const string Groq = "groq";
    public const string DeepSeek = "deepseek";
    public const string XAi = "xai";
    public const string Mistral = "mistral";

    /// <summary>OpenRouter, one key, many models (e.g. <c>anthropic/claude-…</c>, <c>google/gemini-…</c>).</summary>
    public const string OpenRouter = "openrouter";

    public const string Together = "together";
    public const string Perplexity = "perplexity";

    /// <summary>A local Ollama server.</summary>
    public const string Ollama = "ollama";

    /// <summary>A local LM Studio server.</summary>
    public const string LmStudio = "lmstudio";

    /// <summary>Any other OpenAI-compatible endpoint; <see cref="AiConfig.BaseUrl"/> is required.</summary>
    public const string Custom = "custom";

    public static readonly string[] All =
    [
        OpenAi, Anthropic, Gemini, Groq, DeepSeek, XAi, Mistral, OpenRouter,
        Together, Perplexity, Ollama, LmStudio, Custom
    ];

    public static readonly Uri OpenAiEndpoint = new("https://api.openai.com/v1/");
    public static readonly Uri AnthropicEndpoint = new("https://api.anthropic.com/v1/");
    public static readonly Uri GeminiEndpoint = new("https://generativelanguage.googleapis.com/v1beta/openai/");
    public static readonly Uri GroqEndpoint = new("https://api.groq.com/openai/v1/");
    public static readonly Uri DeepSeekEndpoint = new("https://api.deepseek.com/v1/");
    public static readonly Uri XAiEndpoint = new("https://api.x.ai/v1/");
    public static readonly Uri MistralEndpoint = new("https://api.mistral.ai/v1/");
    public static readonly Uri OpenRouterEndpoint = new("https://openrouter.ai/api/v1/");
    public static readonly Uri TogetherEndpoint = new("https://api.together.xyz/v1/");
    public static readonly Uri PerplexityEndpoint = new("https://api.perplexity.ai/");
    public static readonly Uri OllamaEndpoint = new("http://localhost:11434/v1/");
    public static readonly Uri LmStudioEndpoint = new("http://localhost:1234/v1/");

    /// <summary>A provider preset: its display slug, default endpoint (null = the user must supply one) and whether it authenticates with an API key.</summary>
    public sealed record Preset(string Name, Uri? Endpoint, bool RequiresKey);

    static readonly Dictionary<string, Preset> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        [OpenAi] = new(OpenAi, OpenAiEndpoint, RequiresKey: true),
        [Anthropic] = new(Anthropic, AnthropicEndpoint, RequiresKey: true),
        [Gemini] = new(Gemini, GeminiEndpoint, RequiresKey: true),
        [Groq] = new(Groq, GroqEndpoint, RequiresKey: true),
        [DeepSeek] = new(DeepSeek, DeepSeekEndpoint, RequiresKey: true),
        [XAi] = new(XAi, XAiEndpoint, RequiresKey: true),
        [Mistral] = new(Mistral, MistralEndpoint, RequiresKey: true),
        [OpenRouter] = new(OpenRouter, OpenRouterEndpoint, RequiresKey: true),
        [Together] = new(Together, TogetherEndpoint, RequiresKey: true),
        [Perplexity] = new(Perplexity, PerplexityEndpoint, RequiresKey: true),
        [Ollama] = new(Ollama, OllamaEndpoint, RequiresKey: false),
        [LmStudio] = new(LmStudio, LmStudioEndpoint, RequiresKey: false),
        [Custom] = new(Custom, null, RequiresKey: false)
    };

    public static bool IsKnown(string? provider) =>
        provider is not null && Presets.ContainsKey(provider);

    /// <summary>The preset for a provider slug, or <c>null</c> when it is unknown.</summary>
    public static Preset? Find(string? provider) =>
        provider is not null && Presets.TryGetValue(provider, out Preset? preset) ? preset : null;
}
