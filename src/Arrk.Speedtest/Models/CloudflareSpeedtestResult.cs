namespace Arrk.Speedtest.Models;

public record CloudflareSpeedtestResult
{
    public double DownloadMbps { get; init; }
    public double UploadMbps { get; init; }
    public double PingMs { get; init; }
    public double JitterMs { get; init; }
    public DateTime TestedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsError => ErrorMessage is not null;
}
