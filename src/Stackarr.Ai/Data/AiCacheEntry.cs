namespace Stackarr.Ai.Data;

/// <summary>
/// A cached model response, keyed by a hash of the feature, model and prompt. Used for ranking,
/// recommendations and other bounded prompts where the same input is likely to recur.
/// </summary>
public sealed class AiCacheEntry
{
    /// <summary>SHA-256 hex of <c>feature|model|prompt</c>.</summary>
    public string Key { get; set; } = "";

    public string Feature { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
