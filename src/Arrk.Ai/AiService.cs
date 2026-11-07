using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Ai;

/// <summary>
/// The single entry point for AI-backed behaviour. A feature checks <see cref="Enabled"/> and, when
/// on, calls <see cref="AskAsync"/> or <see cref="AskJsonAsync{T}"/>, otherwise it keeps its
/// deterministic path. AI is <b>advisory</b>: never let a model's answer be the final authority on
/// something a deterministic check can verify.
///
/// The client is rebuilt when the <c>ai</c> configuration reloads, so enabling AI or changing its
/// model does not require an application restart. Every call is recorded through
/// <see cref="AiUsageService"/>; usage accounting can never break a feature.
/// </summary>
public sealed class AiService : IDisposable
{
    readonly AiClientFactory _factory;
    readonly ILogger<AiService> _logger;
    readonly AiUsageService? _usage;
    readonly AiBudget? _budget;
    readonly IDisposable? _reloadSubscription;
    readonly object _gate = new();

    AiConfig _config;
    IChatClient? _chat;
    IChatClient? _fast;
    IChatClient? _high;

    public AiService(
        AiClientFactory factory,
        IOptionsMonitor<AiConfig> options,
        ILogger<AiService> logger,
        AiUsageService? usage = null,
        AiBudget? budget = null)
    {
        _factory = factory;
        _logger = logger;
        _usage = usage;
        _budget = budget;
        _config = options.CurrentValue;

        Rebuild();
        _reloadSubscription = options.OnChange((config, _) =>
        {
            _config = config;
            Rebuild();
        });
    }

    /// <summary>Whether a usable client is configured. Features must honour this.</summary>
    public bool Enabled
    {
        get
        {
            lock (_gate)
                return _chat is not null;
        }
    }

    /// <summary>The effective configuration (the current snapshot, updated on reload).</summary>
    public AiConfig Config => _config;

    /// <summary>The balanced default client, for streaming and tool calling.</summary>
    public IChatClient? MediumClient
    {
        get
        {
            lock (_gate)
                return _chat;
        }
    }

    /// <summary>Compatibility accessor for the former conversation profile.</summary>
    public IChatClient? ChatClient => MediumClient;

    /// <summary>The cheaper client for ranking/parsing, or Medium when no low model is set.</summary>
    public IChatClient? LowClient
    {
        get
        {
            lock (_gate)
                return _fast;
        }
    }

    /// <summary>Compatibility accessor for the former Fast profile.</summary>
    public IChatClient? FastClient => LowClient;

    /// <summary>The more capable client, or Medium when no high model is set.</summary>
    public IChatClient? HighClient
    {
        get
        {
            lock (_gate)
                return _high;
        }
    }

    /// <summary>
    /// Sends a single-turn prompt, prepending the configured system prompt. Returns the model's text,
    /// or <c>null</c> when AI is disabled, over budget, or the prompt is blank.
    /// </summary>
    public Task<string?> AskAsync(string prompt, CancellationToken ct = default) =>
        AskAsync(prompt, AiModelRole.Medium, AiFeatureKeys.Core, "ask", null, ct);

    /// <summary>
    /// Sends a single-turn prompt on a specific model profile and attributes the usage to
    /// <paramref name="feature"/>/<paramref name="operation"/>. Exceptions from the provider
    /// propagate so callers can fall back explicitly.
    /// </summary>
    public async Task<string?> AskAsync(
        string prompt,
        AiModelRole role,
        string feature,
        string operation,
        int? userId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        IChatClient? client = ClientFor(role);
        if (client is null)
            return null;

        if (!await CanCallAsync(feature, operation, role, userId, ct))
            return null;

        long started = Stopwatch.GetTimestamp();
        try
        {
            ChatResponse response = await client.GetResponseAsync(Messages(prompt), cancellationToken: ct);
            string? text = response.Text;
            await RecordAsync(feature, operation, role, response.Usage, started, true, false, userId, null, prompt, text, ct);
            return text;
        }
        catch (Exception ex)
        {
            await RecordAsync(feature, operation, role, null, started, false, false, userId, Describe(ex), prompt, null, ct);
            throw;
        }
    }

    /// <summary>
    /// Sends a prompt and deserialises a JSON answer. Returns <c>null</c> when AI is disabled, over
    /// budget, the prompt is blank, or the response cannot be parsed.
    /// </summary>
    public async Task<T?> AskJsonAsync<T>(
        string prompt,
        AiModelRole role,
        string feature,
        string operation,
        int? userId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return default;

        IChatClient? client = ClientFor(role);
        if (client is null)
            return default;

        if (!await CanCallAsync(feature, operation, role, userId, ct))
            return default;

        long started = Stopwatch.GetTimestamp();
        try
        {
            ChatOptions options = new() { ResponseFormat = ChatResponseFormat.Json };
            ChatResponse response = await client.GetResponseAsync(Messages(prompt), options, ct);
            string? text = response.Text;
            T? parsed = AiJson.TryParse<T>(text);

            // A response that cannot be parsed is a model failure, not a success.
            await RecordAsync(feature, operation, role, response.Usage, started, parsed is not null, false,
                userId, parsed is null ? "unparseable response" : null, prompt, text, ct);

            return parsed;
        }
        catch (Exception ex)
        {
            await RecordAsync(feature, operation, role, null, started, false, false, userId, Describe(ex), prompt, null, ct);
            throw;
        }
    }

    public IChatClient? ClientFor(AiModelRole role)
    {
        lock (_gate)
            return role switch
            {
                AiModelRole.Low => _fast ?? _chat,
                AiModelRole.High => _high ?? _chat,
                _ => _chat
            };
    }

    void Rebuild()
    {
        IChatClient? chat;
        IChatClient? fast;
        IChatClient? high;
        try
        {
            chat = _factory.Create(_config, AiModelRole.Medium);
            fast = chat is null || _config.ResolveLowModel() == _config.ResolveMediumModel()
                ? chat : _factory.Create(_config, AiModelRole.Low);
            high = chat is null || _config.ResolveHighModel() == _config.ResolveMediumModel()
                ? chat : _config.ResolveHighModel() == _config.ResolveLowModel()
                    ? fast : _factory.Create(_config, AiModelRole.High);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create the AI client; AI features stay off");
            chat = null;
            fast = null;
            high = null;
        }

        lock (_gate)
        {
            _chat = chat;
            _fast = fast;
            _high = high;
        }

        if (chat is null)
            _logger.LogDebug("AI features are disabled");
        else
            _logger.LogInformation("AI features enabled (provider: {Provider}, medium: {Medium}, low: {Low}, high: {High})",
                _config.Provider, _config.ResolveMediumModel(), _config.ResolveLowModel(), _config.ResolveHighModel());
    }

    async Task<bool> CanCallAsync(string feature, string operation, AiModelRole role, int? userId, CancellationToken ct)
    {
        if (_budget is null)
            return true;

        try
        {
            if (await _budget.CanCallAsync(_config, ct))
                return true;
        }
        catch (Exception ex)
        {
            // Usage storage being unavailable must not disable AI altogether.
            _logger.LogDebug(ex, "Could not evaluate the AI budget");
            return true;
        }

        _logger.LogInformation("AI budget exhausted; skipped {Feature}/{Operation}", feature, operation);
        await RecordAsync(feature, operation, role, null, Stopwatch.GetTimestamp(), false, false, userId, "budget", null, null, ct);
        return false;
    }

    List<ChatMessage> Messages(string prompt)
    {
        List<ChatMessage> messages = [];
        if (!string.IsNullOrWhiteSpace(_config.SystemPrompt))
            messages.Add(new ChatMessage(ChatRole.System, _config.SystemPrompt));
        messages.Add(new ChatMessage(ChatRole.User, prompt));
        return messages;
    }

    async Task RecordAsync(
        string feature,
        string operation,
        AiModelRole role,
        UsageDetails? usage,
        long started,
        bool success,
        bool cacheHit,
        int? userId,
        string? error,
        string? prompt,
        string? response,
        CancellationToken ct)
    {
        if (_usage is null)
            return;

        long durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        try
        {
            await _usage.RecordAsync(_usage.CreateRecord(
                _config,
                feature,
                operation,
                role,
                usage?.InputTokenCount ?? 0,
                usage?.OutputTokenCount ?? 0,
                durationMs,
                success,
                cacheHit,
                userId,
                error,
                prompt,
                response), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record AI usage for {Feature}/{Operation}", feature, operation);
        }
    }

    static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    public void Dispose() => _reloadSubscription?.Dispose();
}
