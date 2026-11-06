namespace Arrk.Core.Models;

public class ServiceHealth
{
    public int Id { get; set; }
    public string ServiceName { get; set; } = "";
    public bool IsHealthy { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CheckedAt { get; set; }
}
