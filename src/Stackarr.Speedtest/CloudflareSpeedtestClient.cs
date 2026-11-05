using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Stackarr.Speedtest.Models;

namespace Stackarr.Speedtest;

public class CloudflareSpeedtestClient(
    HttpClient httpClient,
    ILogger<CloudflareSpeedtestClient> logger)
{
    const string DefaultDownloadUrl = "https://speed.cloudflare.com/__down";
    const string DefaultUploadUrl = "https://speed.cloudflare.com/__up";
    const double BitsPerMegabit = 1_000_000.0;
    const int MaxRequestAttempts = 3;


    public string DownloadUrl { get; init; } = DefaultDownloadUrl;
    public string UploadUrl { get; init; } = DefaultUploadUrl;

    /// <summary>
    /// Overrides the test sizing/latency loop for fast integration tests. Defaults to the
    /// production Cloudflare-oriented profile.
    /// </summary>
    public SpeedtestProfile Profile { get; init; } = SpeedtestProfile.Default;

    /// <summary>
    /// Runs the test over the client injected via DI (Stackarr's own network).
    /// </summary>
    public Task<CloudflareSpeedtestResult> RunTestAsync(CancellationToken ct = default) =>
        RunTestAsync(httpClient, ct);

    /// <summary>
    /// Runs the test over a caller-supplied client. Used to measure a different path, e.g. a
    /// client whose proxy routes through the VPN (Gluetun) container.
    /// </summary>
    public async Task<CloudflareSpeedtestResult> RunTestAsync(HttpClient client, CancellationToken ct = default)
    {
        try
        {
            List<SpeedtestMeasurement> measurements = [];

            measurements.AddRange(await RunLatencyTestsAsync(client, ct));
            measurements.AddRange(await RunBandwidthTestsAsync(
                client, Profile.DownloadPayloadSizes, Profile.DownloadIterations, TestDirection.Download, ct));
            measurements.AddRange(await RunBandwidthTestsAsync(
                client, Profile.UploadPayloadSizes, Profile.UploadIterations, TestDirection.Upload, ct));

            IReadOnlyList<double> latencies = measurements
                .Where(m => m.Category == TestCategory.Latency)
                .Select(m => m.LatencyMs)
                .ToList();

            IReadOnlyList<double> downloadSpeeds = measurements
                .Where(m => m.Category == TestCategory.Speed && m.Direction == TestDirection.Download)
                .Select(m => m.BitsPerSecond)
                .ToList();

            IReadOnlyList<double> uploadSpeeds = measurements
                .Where(m => m.Category == TestCategory.Speed && m.Direction == TestDirection.Upload)
                .Select(m => m.BitsPerSecond)
                .ToList();

            return new CloudflareSpeedtestResult
            {
                DownloadMbps = Percentile(downloadSpeeds, 0.9) / BitsPerMegabit,
                UploadMbps = Percentile(uploadSpeeds, 0.9) / BitsPerMegabit,
                PingMs = Median(latencies),
                JitterMs = CalculateJitter(latencies),
                TestedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Speed test failed");
            return new CloudflareSpeedtestResult
            {
                TestedAt = DateTime.UtcNow,
                ErrorMessage = ex.Message
            };
        }
    }

    async Task<List<SpeedtestMeasurement>> RunLatencyTestsAsync(HttpClient client, CancellationToken ct)
    {
        List<SpeedtestMeasurement> results = [];

        for (int i = 0; i < Profile.LatencyIterations; i++)
        {
            if (ct.IsCancellationRequested) break;

            (TimeSpan duration, double latencyMs) = await MeasureRequestAsync(
                client, $"{DownloadUrl}?bytes=0", HttpMethod.Get, null, ct);

            results.Add(new SpeedtestMeasurement(
                TestCategory.Latency,
                TestDirection.Download,
                0,
                duration,
                latencyMs,
                0));

            await Task.Delay(50, ct);
        }

        return results;
    }

    async Task<List<SpeedtestMeasurement>> RunBandwidthTestsAsync(
        HttpClient client,
        int[] payloadSizes,
        int[] iterations,
        TestDirection direction,
        CancellationToken ct)
    {
        List<SpeedtestMeasurement> results = [];

        for (int i = 0; i < payloadSizes.Length; i++)
        {
            int size = payloadSizes[i];
            int count = iterations[i];

            for (int j = 0; j < count; j++)
            {
                if (ct.IsCancellationRequested) break;

                string url = direction == TestDirection.Download
                    ? $"{DownloadUrl}?bytes={size}"
                    : UploadUrl;

                byte[]? uploadBytes = direction == TestDirection.Upload
                    ? new byte[size]
                    : null;

                (TimeSpan duration, double latencyMs) = await MeasureRequestAsync(
                    client,
                    url,
                    direction == TestDirection.Download ? HttpMethod.Get : HttpMethod.Post,
                    uploadBytes,
                    ct);

                double bitsPerSecond = duration.TotalSeconds > 0
                    ? size * 8.0 / duration.TotalSeconds
                    : 0;

                results.Add(new SpeedtestMeasurement(
                    TestCategory.Speed,
                    direction,
                    size,
                    duration,
                    latencyMs,
                    bitsPerSecond));

                await Task.Delay(50, ct);
            }
        }

        return results;
    }

    async Task<(TimeSpan Duration, double LatencyMs)> MeasureRequestAsync(
        HttpClient client,
        string url,
        HttpMethod method,
        byte[]? uploadBytes,
        CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(method, url);
                if (uploadBytes is not null)
                {
                    ByteArrayContent content = new(uploadBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Content = content;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                using HttpResponseMessage response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                _ = await response.Content.ReadAsByteArrayAsync(ct);
                stopwatch.Stop();

                double serverTimeMs = 0;
                if (response.Headers.TryGetValues("Server-Timing", out IEnumerable<string>? values))
                {
                    string? timingHeader = values.FirstOrDefault(v => v.Contains("cfRequestDuration"));
                    if (timingHeader is not null)
                    {
                        int durIndex = timingHeader.IndexOf("dur=", StringComparison.Ordinal);
                        if (durIndex >= 0)
                        {
                            string durStr = timingHeader[(durIndex + 4)..];
                            int semicolonIndex = durStr.IndexOf(';');
                            if (semicolonIndex >= 0) durStr = durStr[..semicolonIndex];
                            if (double.TryParse(durStr, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double dur))
                            {
                                serverTimeMs = dur;
                            }
                        }
                    }
                }

                double latencyMs = Math.Max(0, stopwatch.Elapsed.TotalMilliseconds - serverTimeMs);
                return (stopwatch.Elapsed, latencyMs);
            }
            catch (Exception ex) when (attempt < MaxRequestAttempts && IsTransient(ex, ct))
            {
                // The VPN's HTTP proxy intermittently fails a CONNECT while the tunnel is
                // hale. A single hiccup must not fail the whole 60+ request measurement.
                logger.LogDebug(
                    ex, "Speed test request attempt {Attempt}/{Max} failed; retrying",
                    attempt, MaxRequestAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
    }

    static bool IsTransient(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested
        && ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException;

    static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;

        List<double> sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;

        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;

        List<double> sorted = values.OrderBy(v => v).ToList();
        double index = percentile * (sorted.Count - 1);
        int floor = (int)Math.Floor(index);
        int ceil = (int)Math.Ceiling(index);

        if (floor == ceil) return sorted[floor];

        double fraction = index - floor;
        return sorted[floor] + fraction * (sorted[ceil] - sorted[floor]);
    }

    static double CalculateJitter(IReadOnlyList<double> latencies)
    {
        if (latencies.Count < 2) return 0;

        return latencies
            .Skip(1)
            .Zip(latencies, (a, b) => Math.Abs(a - b))
            .Average();
    }
}
