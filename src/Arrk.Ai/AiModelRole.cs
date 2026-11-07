namespace Arrk.Ai;

/// <summary>Which configured model profile a request should use.</summary>
public enum AiModelRole
{
    /// <summary>Balanced conversation and user-facing work.</summary>
    Medium = 0,

    /// <summary>Cheap ranking, parsing and summaries.</summary>
    Low = 1,

    /// <summary>More capable reasoning for demanding user requests.</summary>
    High = 2
}
