using Hangfire;

namespace Stackarr.Orchestration.Jobs;

public class OrphanCleanupJob(ResourceInventory inventory)
{
    public const string DefaultCron = "* * * * *";

    [JobDisplayName("Orphan Cleanup")]
    public Task Execute(CancellationToken ct)
        => inventory.CleanupOrphanedResources(ct);
}
