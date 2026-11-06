namespace Arrk.Ai.Data;

/// <summary>Per-user assistant preference: the selected persona.</summary>
public sealed class AiUserPreference
{
    public int UserId { get; set; }
    public string PersonaKey { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
