namespace Stackarr.Ai.Data;

/// <summary>
/// One recorded AI call. Written for every request (automatic or chat), including failures and
/// cache hits, so the admin page can show what is actually being spent and where.
/// Prompt/response text is never stored unless <c>ai.privacy.storePromptText</c> is enabled.
/// </summary>
public sealed class AiUsageRecord
{
    public long Id { get; set; }

    public DateTime Timestamp { get; set; }

    /// <summary>Stable feature key, e.g. <c>releaseRanking</c> or <c>assistant</c>.</summary>
    public string Feature { get; set; } = "";

    /// <summary>The specific operation, e.g. <c>music.releaseRank</c> or <c>library.search</c>.</summary>
    public string Operation { get; set; } = "";

    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";

    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long DurationMs { get; set; }

    public bool Success { get; set; }
    public bool CacheHit { get; set; }

    /// <summary>The user who triggered an on-demand call. Null for automatic/background calls.</summary>
    public int? UserId { get; set; }

    /// <summary>Short error code/message; never a prompt.</summary>
    public string? Error { get; set; }

    /// <summary>Only populated when prompt storage is explicitly enabled.</summary>
    public string? PromptText { get; set; }

    public string? ResponseText { get; set; }
}
