using Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace Stackarr.DownloadManagement.Jobs;

public class SeedingLimitCleanupJob(IServiceScopeFactory scopeFactory)
{
    public const string DefaultCron = "*/5 * * * *";

    [JobDisplayName("Seeding Limit Cleanup")]
    public async Task Execute(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var cleanup = scope.ServiceProvider.GetRequiredService<SeedingLimitCleanup>();
        await cleanup.CleanupCompletedSeeding(ct);
    }
}
