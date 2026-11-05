namespace Stackarr.Ai;

/// <summary>
/// Enforces the global AI budget before a call is made. Automatic features silently skip when the
/// budget is exhausted; the admin page shows why. A limit of <c>0</c> means unlimited.
/// </summary>
public sealed class AiBudget(AiUsageService usage, TimeProvider time)
{
    public async Task<bool> CanCallAsync(AiConfig config, CancellationToken ct = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        DateTime dayStart = new(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

        if (config.Limits.MaxRequestsPerDay > 0)
        {
            int calls = await usage.CountSinceAsync(dayStart, ct);
            if (calls >= config.Limits.MaxRequestsPerDay)
                return false;
        }

        if (config.Limits.MaxTokensPerMonth > 0)
        {
            DateTime monthStart = new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            long tokens = await usage.TokensSinceAsync(monthStart, ct);
            if (tokens >= config.Limits.MaxTokensPerMonth)
                return false;
        }

        return true;
    }

    /// <summary>Remaining requests today, or <c>null</c> when the cap is unlimited.</summary>
    public async Task<int?> RemainingRequestsTodayAsync(AiConfig config, CancellationToken ct = default)
    {
        if (config.Limits.MaxRequestsPerDay <= 0)
            return null;

        DateTime now = time.GetUtcNow().UtcDateTime;
        DateTime dayStart = new(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        int calls = await usage.CountSinceAsync(dayStart, ct);
        return Math.Max(0, config.Limits.MaxRequestsPerDay - calls);
    }
}
