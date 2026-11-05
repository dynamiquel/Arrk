using Hangfire;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Jobs;

/// <summary>
/// Removes abandoned local-upload staging folders and their rows. Successful imports are cleaned
/// up as soon as the arr finishes; this sweep covers failed attempts, cancelled dialogs and files
/// left behind by a restart.
/// </summary>
public class TvUploadCleanupJob(TvUploadStore store)
{
    public const string DefaultCron = "17 4 * * *";

    [JobDisplayName("Local Upload Cleanup")]
    public Task Execute(CancellationToken ct) => store.CleanupExpiredAsync(ct);
}
