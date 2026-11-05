using Stackarr.Core.Models;

namespace Stackarr.Orchestration;

/// <summary>
/// Shares one dashboard summary across the orchestration tiles so a dashboard with several
/// service/host/docker tiles still performs a single poll per second rather than one per tile.
/// </summary>
public sealed class DashboardSummaryCache(DashboardService dashboard, TimeProvider time)
{
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(1);

    readonly SemaphoreSlim _gate = new(1, 1);
    DashboardSummary? _cached;
    DateTimeOffset _expires;

    public async Task<DashboardSummary> GetAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (_cached is not null && now < _expires)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            now = time.GetUtcNow();
            if (_cached is not null && now < _expires)
                return _cached;

            _cached = await dashboard.GetDashboardSummary(ct);
            _expires = now + Lifetime;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
