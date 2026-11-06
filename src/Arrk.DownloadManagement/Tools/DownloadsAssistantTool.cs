using System.Text.Json;
using Microsoft.Extensions.AI;
using Arrk.Ai;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Slskd;

namespace Arrk.DownloadManagement.Tools;

/// <summary>
/// Download progress and control, owned by the download-management module. Progress is read for any
/// signed-in user; pausing/resuming a torrent is an administrator action because it changes shared
/// download state. There is no library id on a torrent, so title matching is against the release
/// name and category (the same text the Downloads page shows).
/// </summary>
public sealed class DownloadsAssistantTool : IAssistantTool
{
    public string Key => "downloads";
    public string Name => "Downloads";
    public string Description => "Read download progress, recent failures, and (admin) pause or resume torrents.";
    public AssistantAccess Access => AssistantAccess.User;

    public IReadOnlyList<string> Keywords =>
    [
        "download", "downloading", "progress", "stalled", "slow", "torrent", "soulseek", "queue",
        "paused", "seeding", "eta", "transfer"
    ];

    public bool IsAvailable => true;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string? query = null, string? status = null, int limit = 15, CancellationToken ct = default) =>
                    await ProgressAsync(context, query, status, limit, ct),
                "downloads_progress",
                "Lists torrent and Soulseek transfers with progress, speed, ETA and state. Use query to match a " +
                "release/library title (e.g. 'Severance') and status=downloading|seeding|paused|error|active. " +
                "This is the tool for 'how is my download going' or 'is X downloading'."),

            AIFunctionFactory.Create(
                async (int limit = 15, CancellationToken ct = default) =>
                    await FailuresAsync(context, limit, ct),
                "downloads_failures",
                "Lists recent torrent failures, stalls, slow transfers and penalty removals from the download event log."),

            AIFunctionFactory.Create(
                async (string id, CancellationToken ct = default) =>
                    await ChangeStateAsync(context, id, pause: true, ct),
                "downloads_pause",
                "Pauses one torrent by its hash/id (from downloads_progress). Administrator only."),

            AIFunctionFactory.Create(
                async (string id, CancellationToken ct = default) =>
                    await ChangeStateAsync(context, id, pause: false, ct),
                "downloads_resume",
                "Resumes one paused torrent by its hash/id (from downloads_progress). Administrator only.")
        ];
    }

    static async Task<JsonElement> ProgressAsync(
        AssistantToolContext context, string? query, string? status, int limit, CancellationToken ct)
    {
        ITorrentDownloader? torrents = Resolve<ITorrentDownloader>(context);
        if (torrents is null)
            return Json(new { error = "The torrent downloader is not available." });

        string filter = string.IsNullOrWhiteSpace(status) ? "active" : status.Trim().ToLowerInvariant();
        int take = Math.Clamp(limit <= 0 ? 15 : limit, 1, 50);

        List<TorrentItem> all;
        TorrentTransferStats transfer;
        try
        {
            all = await torrents.GetTorrents("all", ct);
            transfer = await torrents.GetTransferStats(ct);
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Could not read downloads: {ex.Message}" });
        }

        List<object> items = all
            .Where(t => MatchesStatus(t, filter))
            .Where(t => MatchesQuery(query, t.Name, t.Category, string.Join(' ', t.Labels)))
            .OrderByDescending(t => t.State == TorrentState.Downloading)
            .ThenByDescending(t => t.Progress)
            .Take(take)
            .Select(t => (object)new
            {
                id = t.Hash,
                source = "torrent",
                name = t.Name,
                category = t.Category,
                state = t.State.ToString(),
                progressPercent = (int)Math.Round(t.Progress * 100),
                downloadedBytes = t.Downloaded,
                sizeBytes = t.Size,
                downloadBytesPerSecond = (long)t.DownloadSpeed,
                uploadBytesPerSecond = (long)t.UploadSpeed,
                eta = t.RemainingTime,
                seeds = t.Seeds,
                peers = t.Peers,
                ratio = Math.Round(t.Ratio, 2),
                addedOn = t.AddedOn
            })
            .ToList();

        SlskdService? slskd = Resolve<SlskdService>(context);
        if (slskd is { IsEnabled: true } && filter is "active" or "downloading" or "queued" or "error" or "all")
        {
            try
            {
                List<SlskdDownload> soulseek = await slskd.GetDownloadsAsync(ct);
                items.AddRange(soulseek
                    .Where(d => MatchesQuery(query, d.FileName, d.DirectoryName, d.Username))
                    .Where(d => filter == "all" || (filter == "error" ? d.IsFailed : !d.IsComplete && (filter != "queued" || d.IsQueued)))
                    .Take(take)
                    .Select(d => (object)new
                    {
                        id = d.Id,
                        source = "soulseek",
                        username = d.Username,
                        name = d.FileName,
                        state = d.IsFailed ? "Failed" : d.IsComplete ? "Completed" : d.IsQueued ? "Queued" : "Downloading",
                        progressPercent = (int)Math.Round(d.PercentComplete),
                        downloadedBytes = d.BytesTransferred,
                        sizeBytes = d.Size,
                        directory = d.DirectoryName,
                        detail = d.Exception
                    }));
            }
            catch
            {
                // A Soulseek outage must not make torrent progress unavailable.
            }
        }

        return Json(new
        {
            count = items.Count,
            filter,
            query,
            transfer = new
            {
                downloadBytesPerSecond = transfer.DownloadSpeed,
                uploadBytesPerSecond = transfer.UploadSpeed,
                totalDownloadedBytes = transfer.TotalDownloaded,
                totalUploadedBytes = transfer.TotalUploaded
            },
            items
        });
    }

    static async Task<JsonElement> FailuresAsync(
        AssistantToolContext context, int limit, CancellationToken ct)
    {
        IEventStore? events = Resolve<IEventStore>(context);
        if (events is null)
            return Json(new { error = "The event log is not available." });

        int take = Math.Clamp(limit <= 0 ? 15 : limit, 1, 50);
        DateTime since = DateTime.UtcNow.AddDays(-7);
        List<string> types =
        [
            DownloadEventType.Error,
            DownloadEventType.Struck,
            DownloadEventType.Stalled,
            DownloadEventType.Slow
        ];

        List<FailureRow> failures = [];
        foreach (string type in types)
        {
            try
            {
                IReadOnlyList<SystemEvent> rows = await events.QueryAsync(new SystemEventQuery
                {
                    Category = EventCategory.Download,
                    EventType = type,
                    Since = since,
                    Limit = take
                }, ct);

                failures.AddRange(rows.Select(e => new FailureRow(
                    e.Timestamp, e.EventType, e.Severity.ToString(), e.Message, e.SourceName, e.Source)));
            }
            catch
            {
                // One missing event type must not hide the rest.
            }
        }

        List<object> ordered = failures
            .OrderByDescending(f => f.Timestamp)
            .Take(take)
            .Select(f => (object)new
            {
                timestamp = f.Timestamp,
                type = f.Type,
                severity = f.Severity,
                message = f.Message,
                name = f.Name,
                id = f.Id
            })
            .ToList();

        return Json(new { count = ordered.Count, since, items = ordered });
    }

    static async Task<JsonElement> ChangeStateAsync(
        AssistantToolContext context, string id, bool pause, CancellationToken ct)
    {
        if (!context.IsAdmin)
            return Json(new { ok = false, message = "Only an administrator can pause or resume downloads." });
        if (string.IsNullOrWhiteSpace(id))
            return Json(new { ok = false, message = "A torrent hash/id is required." });

        ITorrentDownloader? torrents = Resolve<ITorrentDownloader>(context);
        if (torrents is null)
            return Json(new { ok = false, message = "The torrent downloader is not available." });

        try
        {
            TorrentItem? torrent = await torrents.GetTorrent(id.Trim(), ct);
            if (torrent is null)
                return Json(new { ok = false, message = "No torrent with that id is known." });

            if (pause)
                await torrents.PauseTorrent(torrent.Hash, ct);
            else
                await torrents.ResumeTorrent(torrent.Hash, ct);

            return Json(new
            {
                ok = true,
                message = $"{(pause ? "Paused" : "Resumed")} {torrent.Name}.",
                id = torrent.Hash,
                name = torrent.Name
            });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = $"Could not {(pause ? "pause" : "resume")} that torrent: {ex.Message}" });
        }
    }

    static bool MatchesStatus(TorrentItem torrent, string status) => status switch
    {
        "downloading" => torrent.State == TorrentState.Downloading,
        "seeding" => torrent.State == TorrentState.Seeding,
        "paused" => torrent.State == TorrentState.Paused,
        "queued" => torrent.State == TorrentState.Queued,
        "error" => torrent.State is TorrentState.Error or TorrentState.MissingFiles,
        "active" => torrent.Progress < 1.0
            && torrent.State is TorrentState.Downloading or TorrentState.Queued or TorrentState.Paused
                or TorrentState.Error or TorrentState.Unknown or TorrentState.MissingFiles,
        _ => true
    };

    static bool MatchesQuery(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        string haystack = string.Join(' ', fields.Where(f => !string.IsNullOrWhiteSpace(f)));
        foreach (string token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    static T? Resolve<T>(AssistantToolContext context) where T : class =>
        context.Services.GetService(typeof(T)) as T;

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    readonly record struct FailureRow(
        DateTime Timestamp, string Type, string Severity, string Message, string? Name, string? Id);
}
