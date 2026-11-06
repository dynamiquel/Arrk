using Hangfire;
using Microsoft.Extensions.Logging;
using Arrk.Core.Modules;
using Arrk.Music.Models;
using Arrk.Music.Services;

namespace Arrk.Music.Jobs;

/// <summary>
/// Drains Arrk's music acquisition queue. Each run it takes a small batch (least-failed first,
/// then oldest), searches and grabs each item, and leaves a failure in the queue with a higher attempt
/// count so it falls behind healthier requests next time. Runs every minute; a request can also kick it
/// to run immediately.
/// </summary>
public class MusicAcquireQueueJob(
    MusicLibrary library,
    MusicAcquisitionService acquisition,
    Arrk.Slskd.SlskdService slskd,
    ILogger<MusicAcquireQueueJob> logger)
{
    public const string DefaultCron = "* * * * *";

    /// <summary>How many queue items one run processes.</summary>
    public const int BatchSize = 10;

    /// <summary>How many are searched at once. Kept low: Soulseek drops the connection under a flood
    /// of searches, which then fails every attempt until it logs back in.</summary>
    public const int MaxConcurrency = 3;

    /// <summary>
    /// Wall-clock cap on one queue item (its Soulseek title search, the artist fallback and any torrent
    /// search combined). A search that runs this long is treated as a failed attempt so it stops
    /// holding a slot and falls behind healthier requests. Settable so tests can shorten it.
    /// </summary>
    public TimeSpan ItemTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Runs the queue now (used when something is requested, so it doesn't wait for the minute).</summary>
    public static void Kick(IBackgroundJobClient? jobs) =>
        jobs?.Enqueue<MusicAcquireQueueJob>(j => j.Execute(CancellationToken.None));

    [SingleFlight]
    [JobDisplayName("Music Acquire Queue")]
    public async Task Execute(CancellationToken ct)
    {
        // While Soulseek is logged out/disconnected (it drops the connection under a search flood),
        // skip the run entirely instead of counting every request as a failed attempt and pushing the
        // whole queue back. The next run (a minute later) retries when it has reconnected.
        if (slskd.IsEnabled && !await slskd.IsAvailableAsync(ct))
        {
            logger.LogInformation("Soulseek is unavailable; leaving the acquisition queue for the next run");
            return;
        }

        List<MusicAcquireQueueItem> batch = await library.TakeAcquireBatchAsync(BatchSize, ct);
        if (batch.Count == 0)
            return;

        int grabbed = 0;
        // Process the batch with bounded concurrency so the slskd search slots (and Prowlarr) are used;
        // a batch of 10 sequential searches would take minutes.
        using SemaphoreSlim gate = new(MaxConcurrency);
        await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct },
            async (item, token) =>
            {
                await gate.WaitAsync(token);
                try
                {
                    // Already held? A subscription re-sync can re-add a song between the import and
                    // Navidrome's next scan; downloading it again would loop forever.
                    if (await library.IsAcquireSatisfiedAsync(item, token))
                    {
                        await library.CompleteAcquireAsync(item.AlbumId, item.DeezerTrackId, token);
                        return;
                    }

                    // Cap the whole attempt; a timeout is just another miss that pushes the row back.
                    using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attempt.CancelAfter(ItemTimeout);
                    MusicGrabResult result = await acquisition.SearchAndGrabAsync(
                        item.Artist, item.Title, item.AlbumId > 0 ? item.AlbumId : null, item.RequireArtist, item.DeezerTrackId, attempt.Token);
                    if (result.Success)
                    {
                        await library.MarkAcquireInFlightAsync(item.Id, token);
                        Interlocked.Increment(ref grabbed);
                    }
                    else
                    {
                        await library.RecordAcquireFailureAsync(item.Id, result.Message, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw; // the job itself was cancelled (shutdown)
                }
                catch (OperationCanceledException)
                {
                    // Per-item timeout: count it as an attempt so the request drops down the queue.
                    logger.LogDebug("Acquisition timed out after {Seconds}s for \"{Title}\"", ItemTimeout.TotalSeconds, item.Title);
                    try { await library.RecordAcquireFailureAsync(item.Id, $"timed out after {ItemTimeout.TotalSeconds:0}s", CancellationToken.None); }
                    catch { /* best effort */ }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Acquisition attempt failed for \"{Title}\"", item.Title);
                    try { await library.RecordAcquireFailureAsync(item.Id, ex.Message, CancellationToken.None); }
                    catch { /* best effort */ }
                }
                finally
                {
                    gate.Release();
                }
            });

        logger.LogInformation("Music acquisition queue: processed {Count} item(s), grabbed {Grabbed}", batch.Count, grabbed);
    }
}
