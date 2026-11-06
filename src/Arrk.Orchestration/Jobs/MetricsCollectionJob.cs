using Hangfire;

namespace Arrk.Orchestration.Jobs;

public class MetricsCollectionJob(ServiceStateManager stateManager)
{
    public const string DefaultCron = "*/5 * * * * *";

    [JobDisplayName("Metrics Collection")]
    public Task Execute(CancellationToken ct)
        => stateManager.RefreshMetrics(ct);
}
