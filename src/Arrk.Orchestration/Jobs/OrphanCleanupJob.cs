using Hangfire;

namespace Arrk.Orchestration.Jobs;

public class OrphanCleanupJob(ResourceInventory inventory)
{
    public const string DefaultCron = "*/10 * * * *";

    [JobDisplayName("Orphan Cleanup")]
    public Task Execute(CancellationToken ct)
        => inventory.CleanupOrphanedResources(ct);
}
