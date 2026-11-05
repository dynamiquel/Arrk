namespace Stackarr.Core.Models;

public class SpeedtestResult
{
    public int Id { get; set; }
    public double DownloadMbps { get; set; }
    public double UploadMbps { get; set; }
    public double PingMs { get; set; }
    public double JitterMs { get; set; }
    public string Target { get; set; } = "network";
    public DateTime TestedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
