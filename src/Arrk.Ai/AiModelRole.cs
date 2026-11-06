namespace Arrk.Ai;

/// <summary>Which configured model profile a request should use.</summary>
public enum AiModelRole
{
    /// <summary>Conversation, explanations and anything user-facing.</summary>
    Chat,

    /// <summary>Cheap ranking, parsing and summaries.</summary>
    Fast
}
