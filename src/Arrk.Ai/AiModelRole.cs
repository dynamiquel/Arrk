namespace Arrk.Ai;

/// <summary>Which configured model profile a request should use.</summary>
public enum AiModelRole
{
    /// <summary>Balanced conversation and user-facing work. Keeps the old Chat numeric value.</summary>
    Medium = 0,

    /// <summary>Cheap ranking, parsing and summaries.</summary>
    Low = 1,

    /// <summary>More capable reasoning for demanding user requests.</summary>
    High = 2,

    /// <summary>Compatibility alias for the former Fast profile.</summary>
    Fast = Low,

    /// <summary>Compatibility alias for callers using the former conversation profile.</summary>
    Chat = Medium
}
