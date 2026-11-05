using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Stackarr.Tvarr.Clients;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// Keeps the arrs' native failed-download redownload in step with
/// <c>services.tv.failedDownloads.retriggerSearch</c>.
///
/// <para>
/// Radarr and Sonarr expose this as <c>autoRedownloadFailed</c> under Download Clients →
/// Download Handling. It only covers failures the arr detects itself (a rejected import, an
/// unpack failure, a download the client reports as failed): the arr blocklists the release and
/// searches again. Stackarr's own errored-torrent cleanup is handled separately by
/// <see cref="DownloadEventHandler"/> + <see cref="TorrentCleanupJob"/>, so both paths share the
/// same switch.
/// </para>
/// </summary>
public class FailedDownloadHandlingSync(
    RadarrClient radarr,
    SonarrClient sonarr,
    ILogger<FailedDownloadHandlingSync> logger)
{
    public async Task<FailedDownloadSyncResult> SyncAsync(bool enabled, CancellationToken ct = default)
    {
        bool radarrChanged = await SafeApplyAsync(radarr, "Radarr", enabled, ct);
        bool sonarrChanged = await SafeApplyAsync(sonarr, "Sonarr", enabled, ct);
        return new(radarrChanged, sonarrChanged);
    }

    async Task<bool> SafeApplyAsync(ArrClient client, string serviceName, bool enabled, CancellationToken ct)
    {
        try
        {
            return await ApplyAsync(client, serviceName, enabled, logger, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // One unreachable arr must not stop the other from being synced.
            logger.LogWarning(ex, "Failed to sync failed-download handling on {Service}", serviceName);
            return false;
        }
    }

    /// <summary>
    /// Ensures <c>autoRedownloadFailed</c> matches <paramref name="enabled"/>. Only writes when the
    /// stored value differs, and preserves every other field the arr returned.
    /// </summary>
    /// <returns><c>true</c> when the arr was updated.</returns>
    public static async Task<bool> ApplyAsync(
        ArrClient client,
        string serviceName,
        bool enabled,
        ILogger logger,
        CancellationToken ct = default)
    {
        JsonObject config = await client.GetDownloadClientConfig(ct);

        bool current = config["autoRedownloadFailed"] is JsonValue value
            && value.TryGetValue(out bool parsed)
            && parsed;

        if (current == enabled)
            return false;

        config["autoRedownloadFailed"] = enabled;
        await client.UpdateDownloadClientConfig(config, ct);

        logger.LogInformation(
            "Set {Service} auto redownload failed to {Enabled}", serviceName, enabled);
        return true;
    }
}

public record FailedDownloadSyncResult(bool RadarrChanged, bool SonarrChanged)
{
    public bool Changed => RadarrChanged || SonarrChanged;
}
