using Arrk.Core.Models;

namespace Arrk.Orchestration.Tests;

public class HostMetricsServiceTests
{
    [Test]
    public async Task GetHostMetrics_ReturnsNonNegativeValues()
    {
        HostMetricsService service = new HostMetricsService();

        HostMetrics metrics = service.GetHostMetrics();

        await Assert.That(metrics.CpuPercent).IsGreaterThanOrEqualTo(0);
        await Assert.That(metrics.MemoryTotalBytes).IsGreaterThan(0);
        await Assert.That(metrics.MemoryUsedBytes).IsGreaterThanOrEqualTo(0);
        await Assert.That(metrics.MemoryPercent).IsGreaterThanOrEqualTo(0);
        await Assert.That(metrics.MemoryPercent).IsLessThanOrEqualTo(100);
    }

}
