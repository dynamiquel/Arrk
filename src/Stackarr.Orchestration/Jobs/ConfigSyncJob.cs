using Hangfire;

namespace Stackarr.Orchestration.Jobs;

/// <summary>
/// Periodic safety net for config reconciliation. The authoritative path is the
/// <see cref="ConfigSyncer"/> options-change subscription (fires immediately when
/// <c>stackarr.yaml</c> changes); this job re-applies the same idempotent reconciliation on a
/// short interval so drift caused by restarts, crashes or out-of-band changes self-heals.
/// </summary>
public class ConfigSyncJob(ConfigSyncer syncer)
{
    public const string DefaultCron = "*/5 * * * * *";

    [JobDisplayName("Config Sync")]
    public Task Execute(CancellationToken ct) => syncer.ForceSyncAsync(ct);
}
