using Hangfire;

namespace Arrk.Orchestration.Jobs;

public class StatusUpdateJob(ServiceStateManager stateManager)
{
    public const string DefaultCron = "*/10 * * * * *";

    [JobDisplayName("Status Updates")]
    public Task Execute(CancellationToken ct)
        => stateManager.GetAllServices(ct);
}
