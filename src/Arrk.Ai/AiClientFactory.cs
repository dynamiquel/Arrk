using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Arrk.Ai;

/// <summary>
/// Builds the optional <see cref="IChatClient"/> from <see cref="AiConfig"/>. AI is advisory and
/// opt-in, so every failure here is non-fatal: the factory logs why and returns <c>null</c>, and the
/// app runs without AI.
///
/// Every provider is OpenAI-compatible, so the endpoint is the only real difference between them;
/// a preset just supplies a default, and <see cref="AiConfig.BaseUrl"/> always wins. That keeps
/// model and vendor choice in configuration and the code free of per-vendor branches.
/// </summary>
/// <param name="logger">Used to explain why AI is off when configuration is unusable.</param>
/// <param name="transport">
/// Optional HTTP transport. Left <c>null</c> in production (the SDK's default transport); tests pass
/// a fake handler to exercise the client without a network.
/// </param>
public sealed class AiClientFactory(ILogger<AiClientFactory> logger, HttpMessageHandler? transport = null)
{
    /// <summary>
    /// The client for the configured provider and model profile, or <c>null</c> when AI is
    /// off/misconfigured. The tiers share one endpoint and differ only by model id.
    /// </summary>
    public IChatClient? Create(AiConfig config, AiModelRole role = AiModelRole.Medium)
    {
        if (!config.Enabled)
            return null;

        AiProviders.Preset? preset = AiProviders.Find(config.Provider);
        if (preset is null)
        {
            logger.LogWarning("AI provider '{Provider}' is not recognised (expected one of: {Known}); AI features stay off",
                config.Provider, string.Join(", ", AiProviders.All));
            return null;
        }

        string model = config.ResolveModel(role);
        if (string.IsNullOrWhiteSpace(model))
        {
            logger.LogWarning("AI is enabled but no model is configured; AI features stay off");
            return null;
        }

        if (!TryResolveEndpoint(config, preset, out Uri? endpoint))
            return null;

        string apiKey = config.ApiKey.Trim();
        if (preset.RequiresKey && apiKey.Length == 0)
        {
            logger.LogWarning("AI provider '{Provider}' requires an API key; AI features stay off", config.Provider);
            return null;
        }

        try
        {
            OpenAIClientOptions options = new() { Endpoint = endpoint };
            if (config.TimeoutSeconds > 0)
                options.NetworkTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

            // One retry is enough for a transient 429/5xx; the default policy can retry for minutes
            // and makes an interactive "Test AI" look hung.
            options.RetryPolicy = new ClientRetryPolicy(maxRetries: 1);
            if (transport is not null)
                options.Transport = new HttpClientPipelineTransport(new HttpClient(transport, disposeHandler: false));

            // The OpenAI client insists on a credential; keyless servers ignore it, so a placeholder is fine.
            OpenAIClient client = new(new ApiKeyCredential(apiKey.Length == 0 ? "unused" : apiKey), options);
            return client.GetChatClient(model).AsIChatClient();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create the AI client for provider '{Provider}'; AI features stay off",
                config.Provider);
            return null;
        }
    }

    /// <summary>
    /// Resolves the endpoint for a configuration: an explicit <see cref="AiConfig.BaseUrl"/> always
    /// wins, otherwise the provider preset's default is used, and a preset with no default (custom)
    /// requires one. Returns <c>false</c> (with a logged reason) when no endpoint can be produced.
    /// </summary>
    public bool TryResolveEndpoint(AiConfig config, out Uri? endpoint) =>
        TryResolveEndpoint(config, AiProviders.Find(config.Provider), out endpoint);

    bool TryResolveEndpoint(AiConfig config, AiProviders.Preset? preset, out Uri? endpoint)
    {
        endpoint = null;

        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out Uri? parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                logger.LogWarning("AI base URL '{BaseUrl}' is not a valid absolute http(s) URL; AI features stay off",
                    config.BaseUrl);
                return false;
            }

            endpoint = parsed;
            return true;
        }

        if (preset?.Endpoint is null)
        {
            logger.LogWarning("AI provider '{Provider}' requires a base URL; AI features stay off", config.Provider);
            return false;
        }

        endpoint = preset.Endpoint;
        return true;
    }
}
