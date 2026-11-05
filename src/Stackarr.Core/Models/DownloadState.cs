namespace Stackarr.Core.Models;

public class DownloadState
{
    public int Id { get; set; }
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public double Progress { get; set; }
    public double DownloadSpeed { get; set; }
    public double UploadSpeed { get; set; }
    public double Ratio { get; set; }
    public int Seeds { get; set; }
    public int Peers { get; set; }
    public string State { get; set; } = "";
    public DateTime AddedAt { get; set; }
    public DateTime LastChecked { get; set; }
    public int SlowStrikeCount { get; set; }
}
