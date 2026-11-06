namespace Arrk.Ai;

/// <summary>The outcome of a per-user assistant limit check, with the numbers the UI can explain.</summary>
public sealed record AssistantLimitStatus(
    bool Allowed,
    string? Message,
    int MessagesThisHour,
    int? MessagesPerHourLimit,
    long TokensToday,
    long? TokensPerDayLimit);

/// <summary>
/// Per-user chat limits. They read the same usage rows the admin page reports, so one assistant
/// message (including all of its tool iterations) counts once and its tokens count once.
///
/// <para>
/// A limit of <c>0</c> means unlimited, matching <see cref="AiLimitsConfig"/> and
/// <see cref="AiBudget"/>.
/// </para>
/// </summary>
public sealed class AssistantRateLimiter(AiUsageService usage, TimeProvider time)
{
    public async Task<AssistantLimitStatus> CheckAsync(AiConfig config, int userId, CancellationToken ct = default)
    {
        AiAssistantFeature limits = config.Features.Assistant;
        DateTime now = time.GetUtcNow().UtcDateTime;

        DateTime hourStart = now.AddHours(-1);
        int messages = await usage.CountForUserAsync(userId, AiFeatureKeys.Assistant, hourStart, ct);

        DateTime dayStart = new(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        long tokens = await usage.TokensForUserAsync(userId, AiFeatureKeys.Assistant, dayStart, ct);

        int? messageLimit = limits.MaxMessagesPerHour > 0 ? limits.MaxMessagesPerHour : null;
        long? tokenLimit = limits.MaxTokensPerUserPerDay > 0 ? limits.MaxTokensPerUserPerDay : null;

        if (messageLimit is { } maxMessages && messages >= maxMessages)
        {
            return new AssistantLimitStatus(false,
                $"You have reached the assistant limit of {maxMessages} messages per hour. Try again later.",
                messages, messageLimit, tokens, tokenLimit);
        }

        if (tokenLimit is { } maxTokens && tokens >= maxTokens)
        {
            return new AssistantLimitStatus(false,
                "You have reached today's assistant token limit. It resets at midnight UTC.",
                messages, messageLimit, tokens, tokenLimit);
        }

        return new AssistantLimitStatus(true, null, messages, messageLimit, tokens, tokenLimit);
    }
}
