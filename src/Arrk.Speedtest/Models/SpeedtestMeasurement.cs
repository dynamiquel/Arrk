namespace Arrk.Speedtest.Models;

internal enum TestCategory
{
    Latency,
    Speed
}

internal enum TestDirection
{
    Download,
    Upload
}

internal record SpeedtestMeasurement(
    TestCategory Category,
    TestDirection Direction,
    int BytesSize,
    TimeSpan Duration,
    double LatencyMs,
    double BitsPerSecond);

/// <summary>
/// Tunable sizing for a speedtest run. The default mirrors the production Cloudflare profile;
/// <see cref="Fast"/> shrinks payloads/iterations so integration tests finish quickly.
/// </summary>
public sealed record SpeedtestProfile
{
    public int[] DownloadPayloadSizes { get; init; } = [100_000, 1_000_000, 10_000_000, 25_000_000];
    public int[] DownloadIterations { get; init; } = [10, 8, 6, 4];
    public int[] UploadPayloadSizes { get; init; } = [100_000, 1_000_000, 10_000_000];
    public int[] UploadIterations { get; init; } = [8, 6, 4];
    public int LatencyIterations { get; init; } = 20;

    public static SpeedtestProfile Default { get; } = new();

    public static SpeedtestProfile Fast { get; } = new()
    {
        DownloadPayloadSizes = [256],
        DownloadIterations = [2],
        UploadPayloadSizes = [256],
        UploadIterations = [2],
        LatencyIterations = 2
    };
}
