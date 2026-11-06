using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Services;

namespace Arrk.Slskd;

/// <summary>
/// App-facing wrapper around <see cref="SlskdClient"/> that guards on the module being enabled and
/// the container being healthy.
/// </summary>
public class SlskdService(
    SlskdClient client,
    IOptionsMonitor<SlskdConfig> config,
    ServiceRegistry registry,
    ILogger<SlskdService> logger)
{
    public bool IsEnabled => config.CurrentValue.Enabled;

    /// <summary>True when a completed Soulseek transfer should be removed once it has been imported.</summary>
    public bool RemoveCompletedAfterImport => config.CurrentValue.RemoveCompletedAfterImport;

    public SlskdClient Client => client;

    bool IsHealthy() => !IsEnabled || registry.Get(SlskdServiceDefinition.StaticKey)?.State.IsHealthy != false;

    /// <summary>True when slskd is reachable and connected to Soulseek.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
            return false;
        try
        {
            return await client.PingAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "slskd ping failed");
            return false;
        }
    }

    readonly SemaphoreSlim _searchGate = new(
        Math.Max(1, config.CurrentValue.MaxConcurrentSearches),
        Math.Max(1, config.CurrentValue.MaxConcurrentSearches));

    /// <summary>Searches Soulseek and waits for responses to settle, returning the raw search.</summary>
    public async Task<SlskdSearch?> SearchAsync(string query, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(query))
            return null;

        // Soulseek drops the connection under a flood of searches, so run only a couple at a time and
        // skip entirely while slskd isn't logged in.
        await _searchGate.WaitAsync(ct);
        try
        {
            if (!await client.PingAsync(ct))
                return null;

            string? id = await client.StartSearchAsync(query, ct);
            if (id is null)
                return null;

            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            SlskdSearch? search = null;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(750, ct);
                search = await client.GetSearchAsync(id, ct);
                if (search is null)
                    break;
                // Stop as soon as slskd marks the search complete (the server window closed), even with
                // zero responses, otherwise a no-result search waits out the full client timeout for
                // nothing. A high response count is enough too, so we don't page through huge results.
                if (search.IsComplete)
                    break;
                if (search.Responses.Count >= 40)
                    break;
            }

            _ = client.DeleteSearchAsync(id, CancellationToken.None);
            return search;
        }
        finally
        {
            _searchGate.Release();
        }
    }

    public Task<bool> EnqueueAsync(string username, IEnumerable<SlskdFile> files, CancellationToken ct = default) =>
        client.EnqueueAsync(username, files, ct);

    public Task<List<SlskdDownload>> GetDownloadsAsync(CancellationToken ct = default) =>
        client.GetDownloadsAsync(ct);

    /// <summary>Cancels a queued/active Soulseek download (optionally deleting partial files).</summary>
    public Task<bool> CancelDownloadAsync(string username, string id, bool remove = false, CancellationToken ct = default) =>
        client.CancelDownloadAsync(username, id, remove, ct);

    /// <summary>
    /// Removes failed Soulseek transfers and their partial files when they ended more than
    /// <paramref name="olderThan"/> ago. slskd keeps failed transfers until they are removed
    /// explicitly. Returns how many were removed.
    /// </summary>
    public async Task<int> RemoveFailedDownloadsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return 0;

        List<SlskdDownload> downloads = await GetDownloadsAsync(ct);
        DateTime cutoff = DateTime.UtcNow - olderThan;
        int removed = 0;
        foreach (SlskdDownload download in downloads)
        {
            if (ct.IsCancellationRequested)
                break;
            if (!download.IsFailed)
                continue;

            // Failed transfers record when they ended; fall back to when they started/queued. Skip when
            // the age is unknown so a freshly failed transfer is never removed by mistake.
            DateTime? ended = download.EndedAt ?? download.StartedAt ?? download.EnqueuedAt;
            if (ended is not DateTime at || at > cutoff)
                continue;

            try
            {
                if (await CancelDownloadAsync(download.Username, download.Id, remove: true, ct))
                    removed++;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not remove failed Soulseek transfer {Id}", download.Id);
            }
        }
        return removed;
    }

    public bool IsHealthyOrDisabled() => IsHealthy();

    /// <summary>
    /// Applies the same global speed limits used for torrents to slskd, at run time (MB/s → KiB/s).
    /// Slots (max concurrent transfers) are set via the container environment instead.
    /// </summary>
    public async Task ApplyConfiguredLimitsAsync(int uploadMbps, int downloadMbps, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;
        int uploadKib = uploadMbps > 0 ? uploadMbps * 1024 : 0;
        int downloadKib = downloadMbps > 0 ? downloadMbps * 1024 : 0;
        try
        {
            bool ok = await client.SetTransferLimitsAsync(uploadKib, downloadKib, ct);
            if (!ok)
                logger.LogDebug("slskd rejected the transfer-limit overlay (speed limits are set at container start)");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not apply slskd transfer limits");
        }
    }
}
