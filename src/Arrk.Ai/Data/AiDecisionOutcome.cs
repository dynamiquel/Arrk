namespace Arrk.Ai.Data;

/// <summary>
/// One AI ranking/selection decision compared with the deterministic choice, so the admin page can
/// show whether the model actually helps. The outcome is filled in later by the owning feature
/// (for example a music import marking the grab as imported or mismatched).
/// </summary>
public sealed class AiDecisionOutcome
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }

    public string Feature { get; set; } = "";
    public string Operation { get; set; } = "";

    /// <summary>Stable subject key within the feature, e.g. a music album id.</summary>
    public string Subject { get; set; } = "";

    /// <summary>The thing the user originally asked for (artist + album/track), shown in the admin comparison.</summary>
    public string Request { get; set; } = "";

    /// <summary>The exact bounded context sent to the model (target metadata and candidates).</summary>
    public string Context { get; set; } = "";

    public string Mode { get; set; } = "";
    public string DeterministicChoice { get; set; } = "";
    public string AiChoice { get; set; } = "";
    public double Confidence { get; set; }
    public string? Reason { get; set; }

    /// <summary>True when the AI choice replaced the deterministic one.</summary>
    public bool Applied { get; set; }

    /// <summary>Filled in by the feature: imported, mismatch, failed, blacklisted…</summary>
    public string? Outcome { get; set; }

    public DateTime? OutcomeAt { get; set; }
}
