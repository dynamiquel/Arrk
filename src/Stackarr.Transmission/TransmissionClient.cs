using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stackarr.Core.Services;

namespace Stackarr.Transmission;

public class TransmissionClient : IDisposable
{
    readonly HttpClient _http;
    readonly Func<string> _baseUrlProvider;
    readonly string _username;
    readonly string _password;

    string _sessionId = "";
    bool _sessionInitialized;
    readonly TaskCompletionSource _setupComplete = new();

    public TransmissionClient(string url, string username, string password)
        : this(() => url, username, password, null)
    {
    }

    public TransmissionClient(string url, string username, string password, HttpMessageHandler? handler)
        : this(() => url, username, password, handler)
    {
    }

    public TransmissionClient(Func<string> baseUrlProvider, string username, string password, HttpMessageHandler? handler = null)
    {
        _baseUrlProvider = baseUrlProvider;
        _username = username;
        _password = password;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public Task WaitForSetup() => _setupComplete.Task;
    public void MarkSetupComplete() => _setupComplete.TrySetResult();

    public void Dispose() => _http.Dispose();

    HttpRequestMessage CreateRequest(object rpcRequest)
    {
        string json = JsonSerializer.Serialize(rpcRequest);
        // The base URL is resolved per request so the client follows the service between its
        // normal Docker hostname and the Gluetun sidecar when VPN routing is toggled at runtime.
        string baseUrl = _baseUrlProvider();
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl, UriKind.Absolute), "/transmission/rpc"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
        request.Headers.Authorization = new("Basic", credentials);

        if (!string.IsNullOrEmpty(_sessionId))
            request.Headers.Add("X-Transmission-Session-Id", _sessionId);

        return request;
    }

    async Task<TransmissionResponse<T>> SendRequest<T>(object rpcRequest, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(rpcRequest);
        HttpResponseMessage response = await _http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            if (response.Headers.TryGetValues("X-Transmission-Session-Id", out IEnumerable<string>? sessionIds))
                _sessionId = sessionIds.FirstOrDefault() ?? "";

            _sessionInitialized = true;
            request = CreateRequest(rpcRequest);
            response = await _http.SendAsync(request, ct);
        }

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(ct);
        TransmissionResponse<T> result = JsonSerializer.Deserialize<TransmissionResponse<T>>(body) ?? throw new InvalidOperationException("Failed to deserialize Transmission response");

        if (result.Result != "success")
            throw new TransmissionException($"Transmission RPC error: {result.Result}");

        return result;
    }

    async Task<TransmissionResponse> SendRequest(object rpcRequest, CancellationToken ct = default)
    {
        TransmissionResponse<TransmissionResponseArguments> result = await SendRequest<TransmissionResponseArguments>(rpcRequest, ct);
        return new()
        {
            Result = result.Result,
            Arguments = result.Arguments,
            Tag = result.Tag
        };
    }

    public virtual async Task<bool> TestConnection(CancellationToken ct = default)
    {
        try
        {
            TransmissionResponse response = await SendRequest(new { method = "session-get" }, ct);
            _sessionInitialized = true;
            return response.Result == "success";
        }
        catch
        {
            return false;
        }
    }

    public virtual async Task<TransmissionSession> GetSession(CancellationToken ct = default)
    {
        TransmissionResponse<TransmissionSession> response = await SendRequest<TransmissionSession>(new { method = "session-get" }, ct);
        return response.Arguments ?? new();
    }

    public virtual async Task<TransmissionSessionStats> GetSessionStats(CancellationToken ct = default)
    {
        TransmissionResponse<TransmissionSessionStats> response = await SendRequest<TransmissionSessionStats>(new { method = "session-stats" }, ct);
        return response.Arguments ?? new();
    }

    public virtual async Task<List<TransmissionTorrent>> GetTorrents(
        string[]? fields = null,
        TransmissionTorrentSort? sort = null,
        bool descending = false,
        Dictionary<string, bool>? filter = null,
        CancellationToken ct = default)
    {
        string[] fieldsList = fields ?? [
            "id", "name", "hashString", "totalSize", "leftUntilDone",
            "rateDownload", "rateUpload", "uploadedEver", "uploadRatio",
            "seeders", "leechers", "status", "addedDate", "eta",
            "percentDone", "error", "errorString", "doneDate",
            "queuePosition", "bandwidthPriority", "labels", "downloadDir",
            "activityDate", "peersConnected", "secondsSeeding",
            "peersSendingToUs", "peersGettingFromUs"
        ];

        Dictionary<string, object> rpcRequest = new Dictionary<string, object>
        {
            ["method"] = "torrent-get",
            ["arguments"] = new Dictionary<string, object>
            {
                ["fields"] = fieldsList,
                ["sort"] = sort switch
                {
                    TransmissionTorrentSort.Name => "name",
                    TransmissionTorrentSort.AddedDate => "addedDate",
                    TransmissionTorrentSort.RateDownload => "rateDownload",
                    TransmissionTorrentSort.RateUpload => "rateUpload",
                    TransmissionTorrentSort.UploadRatio => "uploadRatio",
                    TransmissionTorrentSort.Progress => "percentDone",
                    TransmissionTorrentSort.QueuePosition => "queuePosition",
                    _ => "id"
                },
                ["sortDescending"] = descending
            }
        };

        if (filter != null && filter.Count > 0)
        {
            ((Dictionary<string, object>)rpcRequest["arguments"])["ids"] =
                await GetFilteredTorrentIds(filter, ct);
        }

        TransmissionResponse response = await SendRequest(rpcRequest, ct);
        return response.Arguments?.Torrents ?? [];
    }

    public virtual async Task<TransmissionTorrent?> GetTorrentById(long id, CancellationToken ct = default)
    {
        List<TransmissionTorrent> torrents = await GetTorrents(filter: new() { ["id"] = true }, ct: ct);
        return torrents.FirstOrDefault(t => t.Id == id);
    }

    async Task<long[]> GetFilteredTorrentIds(Dictionary<string, bool> filter, CancellationToken ct)
    {
        var rpcRequest = new
        {
            method = "torrent-get",
            // "error" is required by MatchesFilter; requesting only id/status made the error filter always empty.
            arguments = new
            {
                fields = new[] { "id", "status", "error" }
            }
        };

        TransmissionResponse response = await SendRequest(rpcRequest, ct);
        List<TransmissionTorrent> allTorrents = response.Arguments?.Torrents ?? [];

        // Transmission's torrent-get has no server-side status filter, so filter client-side.
        return allTorrents
            .Where(t => MatchesFilter(t, filter))
            .Select(t => t.Id)
            .ToArray();
    }

    static bool MatchesFilter(TransmissionTorrent torrent, Dictionary<string, bool> filter)
    {
        foreach (KeyValuePair<string, bool> entry in filter)
        {
            if (!entry.Value)
                continue;

            bool matches = entry.Key switch
            {
                "downloading" => torrent.Status == TransmissionStatus.Downloading
                    || torrent.Status == TransmissionStatus.DownloadWait,
                "seeding" => torrent.Status == TransmissionStatus.Seeding
                    || torrent.Status == TransmissionStatus.SeedWait,
                "paused" => torrent.Status == TransmissionStatus.Stopped,
                "error" => torrent.Error != 0,
                "active" => torrent.Status is TransmissionStatus.Downloading
                    or TransmissionStatus.Seeding,
                "queued" => torrent.Status is TransmissionStatus.CheckWait
                    or TransmissionStatus.DownloadWait
                    or TransmissionStatus.SeedWait,
                _ => true
            };

            if (matches)
                return true;
        }

        return false;
    }

    public virtual async Task<long> AddTorrent(
        byte[] torrentData,
        string? downloadDir = null,
        string[]? labels = null,
        CancellationToken ct = default)
    {
        string base64 = Convert.ToBase64String(torrentData);
        Dictionary<string, object> arguments = new Dictionary<string, object>
        {
            ["metainfo"] = base64
        };

        if (!string.IsNullOrEmpty(downloadDir))
            arguments["download-dir"] = downloadDir;

        if (labels is { Length: > 0 })
            arguments["labels"] = labels;

        TransmissionResponse response = await SendRequest(new { method = "torrent-add", arguments }, ct);
        TransmissionTorrent? torrent = response.Arguments?.Torrent ?? response.Arguments?.TorrentDuplicate;
        return torrent?.Id ?? 0;
    }

    public virtual async Task<long> AddTorrentByUri(
        Uri uri,
        string? downloadDir = null,
        string[]? labels = null,
        CancellationToken ct = default)
    {
        Dictionary<string, object> arguments = new Dictionary<string, object>
        {
            // OriginalString preserves the exact magnet/URL; Uri.ToString() unescapes it, which can
            // corrupt magnet query parameters (e.g. "&dn=" values) and make Transmission reject it.
            ["filename"] = uri.OriginalString
        };

        if (!string.IsNullOrEmpty(downloadDir))
            arguments["download-dir"] = downloadDir;

        if (labels is { Length: > 0 })
            arguments["labels"] = labels;

        TransmissionResponse response = await SendRequest(new { method = "torrent-add", arguments }, ct);
        TransmissionTorrent? torrent = response.Arguments?.Torrent ?? response.Arguments?.TorrentDuplicate;
        return torrent?.Id ?? 0;
    }

    public virtual Task RemoveTorrent(string id, TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles, CancellationToken ct = default)
    {
        return RemoveTorrents([id], fileCleanup, ct);
    }

    public virtual async Task RemoveTorrents(string[] ids, TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-remove",
            arguments = new Dictionary<string, object>
            {
                ["ids"] = ids,
                ["delete-local-data"] = fileCleanup == TorrentFileCleanup.DeleteFiles
            }
        }, ct);
    }

    public virtual async Task PauseTorrent(string id, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-stop",
            arguments = new { ids = new[] { id } }
        }, ct);
    }

    public virtual async Task PauseTorrents(string[] ids, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-stop",
            arguments = new { ids }
        }, ct);
    }

    public virtual async Task ResumeTorrent(string id, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-start",
            arguments = new { ids = new[] { id } }
        }, ct);
    }

    public virtual async Task ResumeTorrents(string[] ids, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-start",
            arguments = new { ids }
        }, ct);
    }

    public virtual async Task PauseAllTorrents(CancellationToken ct = default)
    {
        await SendRequest(new { method = "torrent-stop" }, ct);
    }

    public virtual async Task ResumeAllTorrents(CancellationToken ct = default)
    {
        await SendRequest(new { method = "torrent-start" }, ct);
    }

    public virtual async Task Reannounce(long id, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-reannounce",
            arguments = new { ids = new[] { id } }
        }, ct);
    }

    public virtual async Task ReannounceAll(CancellationToken ct = default)
    {
        await SendRequest(new { method = "torrent-reannounce" }, ct);
    }

    public virtual async Task SetTorrentLocation(long id, string location, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-set-location",
            arguments = new
            {
                ids = new[] { id },
                location,
                move = true
            }
        }, ct);
    }

    /// <summary>Replaces a torrent's labels. Used to mark Stackarr-grabbed music as imported.</summary>
    public virtual async Task SetTorrentLabels(long id, string[] labels, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-set",
            arguments = new Dictionary<string, object>
            {
                ["ids"] = new[] { id },
                ["labels"] = labels
            }
        }, ct);
    }

    public virtual async Task SetTorrentBandwidthPriority(long id, int priority, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-set",
            arguments = new Dictionary<string, object>
            {
                ["ids"] = new[] { id },
                ["bandwidth-priority"] = priority
            }
        }, ct);
    }

    public virtual async Task SetTorrentQueuePosition(long id, int position, CancellationToken ct = default)
    {
        await SendRequest(new
        {
            method = "torrent-set",
            arguments = new Dictionary<string, object>
            {
                ["ids"] = new[] { id },
                ["queue-position"] = position
            }
        }, ct);
    }

    public virtual async Task<int[]> GetQueuePosition(long id, CancellationToken ct = default)
    {
        List<TransmissionTorrent> torrents = await GetTorrents(["id", "queuePosition"], ct: ct);
        return torrents.Where(t => t.Id == id).Select(t => (int)t.QueuePosition).ToArray();
    }

    public virtual async Task<List<TransmissionFileEntry>> GetTorrentFiles(long id, CancellationToken ct = default)
    {
        TransmissionResponse response = await SendRequest(new
        {
            method = "torrent-get",
            arguments = new
            {
                ids = new[] { id },
                fields = new[] { "files", "fileStats" }
            }
        }, ct);

        TransmissionTorrent? torrent = response.Arguments?.Torrents?.FirstOrDefault();
        if (torrent?.Files == null)
            return [];

        List<TransmissionFileEntry> files = new List<TransmissionFileEntry>();
        for (int i = 0; i < torrent.Files.Count; i++)
        {
            TransmissionTorrentFile file = torrent.Files[i];
            TransmissionFileStat? stats = torrent.FileStats?.ElementAtOrDefault(i);
            files.Add(new()
            {
                Index = i,
                Name = file.Name,
                Length = file.Length,
                BytesCompleted = file.BytesCompleted,
                Wanted = stats?.Wanted ?? true,
                Priority = stats?.Priority ?? 0
            });
        }

        return files;
    }

    public virtual async Task<List<TransmissionTrackerEntry>> GetTorrentTrackers(long id, CancellationToken ct = default)
    {
        TransmissionResponse response = await SendRequest(new
        {
            method = "torrent-get",
            arguments = new
            {
                ids = new[] { id },
                fields = new[] { "trackers" }
            }
        }, ct);

        TransmissionTorrent? torrent = response.Arguments?.Torrents?.FirstOrDefault();
        return torrent?.Trackers ?? [];
    }

    public virtual async Task<TransmissionSession> SetSessionLimits(
        int? uploadLimit = null,
        bool? uploadLimitEnabled = null,
        int? downloadLimit = null,
        bool? downloadLimitEnabled = null,
        int? maxActiveDownloads = null,
        int? maxActiveSeeds = null,
        CancellationToken ct = default)
    {
        Dictionary<string, object> arguments = new Dictionary<string, object>();

        if (uploadLimit.HasValue)
            arguments["speed-limit-up"] = uploadLimit.Value;
        if (uploadLimitEnabled.HasValue)
            arguments["speed-limit-up-enabled"] = uploadLimitEnabled.Value;
        if (downloadLimit.HasValue)
            arguments["speed-limit-down"] = downloadLimit.Value;
        if (downloadLimitEnabled.HasValue)
            arguments["speed-limit-down-enabled"] = downloadLimitEnabled.Value;
        if (maxActiveDownloads.HasValue)
        {
            arguments["download-queue-size"] = maxActiveDownloads.Value;
            // Transmission's download queue is on by default; keep it in step with the size above.
            arguments["download-queue-enabled"] = maxActiveDownloads.Value > 0;
        }
        if (maxActiveSeeds.HasValue)
        {
            // The RPC argument is hyphenated ("seed-queue-enabled"). Transmission silently ignores
            // unknown keys, so an underscore here disables the whole seed queue and the size limit
            // is never enforced, torrents then seed unbounded.
            arguments["seed-queue-size"] = maxActiveSeeds.Value;
            arguments["seed-queue-enabled"] = maxActiveSeeds.Value > 0;
        }

        await SendRequest(new { method = "session-set", arguments }, ct);
        return await GetSession(ct);
    }

    public virtual async Task<List<string>> GetLabels(CancellationToken ct = default)
    {
        TransmissionResponse response = await SendRequest(new
        {
            method = "torrent-get",
            arguments = new { fields = new[] { "labels" } }
        }, ct);

        HashSet<string> allLabels = new HashSet<string>();
        if (response.Arguments?.Torrents != null)
        {
            foreach (TransmissionTorrent torrent in response.Arguments.Torrents)
            {
                if (torrent.Labels != null)
                    foreach (string label in torrent.Labels)
                        allLabels.Add(label);
            }
        }

        return allLabels.ToList();
    }

    public void ResetSession()
    {
        _sessionId = "";
        _sessionInitialized = false;
    }
}

public class TransmissionException(string message) : Exception(message);

public enum TransmissionTorrentSort
{
    Id,
    Name,
    AddedDate,
    RateDownload,
    RateUpload,
    UploadRatio,
    Progress,
    QueuePosition
}

public class TransmissionResponse
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("arguments")]
    public TransmissionResponseArguments? Arguments { get; set; }

    [JsonPropertyName("tag")]
    public int Tag { get; set; }
}

public class TransmissionResponse<T>
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("arguments")]
    public T? Arguments { get; set; }

    [JsonPropertyName("tag")]
    public int Tag { get; set; }
}

public class TransmissionResponseArguments
{
    [JsonPropertyName("torrents")]
    public List<TransmissionTorrent>? Torrents { get; set; }

    [JsonPropertyName("session")]
    public TransmissionSession? Session { get; set; }

    [JsonPropertyName("stats")]
    public TransmissionSessionStats? Stats { get; set; }

    [JsonPropertyName("torrent-added")]
    public TransmissionTorrent? Torrent { get; set; }

    [JsonPropertyName("torrent-duplicate")]
    public TransmissionTorrent? TorrentDuplicate { get; set; }

    [JsonPropertyName("removed")]
    public long[]? Removed { get; set; }
}

/// <summary>
/// Transmission RPC status codes (torrent-get "status" field).
/// </summary>
public static class TransmissionStatus
{
    public const int Stopped = 0;
    public const int CheckWait = 1;
    public const int Checking = 2;
    public const int DownloadWait = 3;
    public const int Downloading = 4;
    public const int SeedWait = 5;
    public const int Seeding = 6;
}

public class TransmissionTorrent
{    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("hashString")]
    public string HashString { get; set; } = "";

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("leftUntilDone")]
    public long LeftUntilDone { get; set; }

    [JsonPropertyName("rateDownload")]
    public long RateDownload { get; set; }

    [JsonPropertyName("rateUpload")]
    public long RateUpload { get; set; }

    [JsonPropertyName("uploadedEver")]
    public long UploadedEver { get; set; }

    [JsonPropertyName("uploadRatio")]
    public double UploadRatio { get; set; }

    [JsonPropertyName("secondsSeeding")]
    public long SecondsSeeding { get; set; }

    [JsonPropertyName("peersSendingToUs")]
    public int Seeders { get; set; }

    [JsonPropertyName("peersGettingFromUs")]
    public int Leechers { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("addedDate")]
    public long AddedDate { get; set; }

    [JsonPropertyName("eta")]
    public long Eta { get; set; }

    [JsonPropertyName("percentDone")]
    public double PercentDone { get; set; }

    [JsonPropertyName("error")]
    public int Error { get; set; }

    [JsonPropertyName("errorString")]
    public string ErrorString { get; set; } = "";

    [JsonPropertyName("doneDate")]
    public long DoneDate { get; set; }

    [JsonPropertyName("queuePosition")]
    public long QueuePosition { get; set; }

    [JsonPropertyName("bandwidthPriority")]
    public int BandwidthPriority { get; set; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; set; }

    [JsonPropertyName("activityDate")]
    public long ActivityDate { get; set; }

    [JsonPropertyName("downloadDir")]
    public string DownloadDir { get; set; } = "";

    [JsonPropertyName("peersConnected")]
    public int PeersConnected { get; set; }

    [JsonPropertyName("files")]
    public List<TransmissionTorrentFile>? Files { get; set; }

    [JsonPropertyName("fileStats")]
    public List<TransmissionFileStat>? FileStats { get; set; }

    [JsonPropertyName("trackers")]
    public List<TransmissionTrackerEntry>? Trackers { get; set; }
}

public class TransmissionTorrentFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("length")]
    public long Length { get; set; }

    [JsonPropertyName("bytesCompleted")]
    public long BytesCompleted { get; set; }
}

public class TransmissionFileStat
{
    [JsonPropertyName("bytesCompleted")]
    public long BytesCompleted { get; set; }

    [JsonPropertyName("wanted")]
    public bool Wanted { get; set; }

    [JsonPropertyName("priority")]
    public int Priority { get; set; }
}

public class TransmissionFileEntry
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public long Length { get; set; }
    public long BytesCompleted { get; set; }
    public bool Wanted { get; set; }
    public int Priority { get; set; }
}

public class TransmissionTrackerEntry
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("announce")]
    public string Announce { get; set; } = "";

    [JsonPropertyName("scrape")]
    public string Scrape { get; set; } = "";

    [JsonPropertyName("announceState")]
    public int AnnounceState { get; set; }

    [JsonPropertyName("scrapeState")]
    public int ScrapeState { get; set; }

    [JsonPropertyName("lastAnnounceSucceeded")]
    public bool LastAnnounceSucceeded { get; set; }

    [JsonPropertyName("lastScrapeSucceeded")]
    public bool LastScrapeSucceeded { get; set; }

    [JsonPropertyName("lastAnnouncePeerCount")]
    public int LastAnnouncePeerCount { get; set; }

    [JsonPropertyName("lastAnnounceResult")]
    public string LastAnnounceResult { get; set; } = "";
}

public class TransmissionSession
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("download-dir")]
    public string DownloadDir { get; set; } = "";

    [JsonPropertyName("speed-limit-down")]
    public long SpeedLimitDown { get; set; }

    [JsonPropertyName("speed-limit-down-enabled")]
    public bool SpeedLimitDownEnabled { get; set; }

    [JsonPropertyName("speed-limit-up")]
    public long SpeedLimitUp { get; set; }

    [JsonPropertyName("speed-limit-up-enabled")]
    public bool SpeedLimitUpEnabled { get; set; }

    [JsonPropertyName("download-queue-size")]
    public int DownloadQueueSize { get; set; }

    [JsonPropertyName("download-queue-enabled")]
    public bool DownloadQueueEnabled { get; set; }

    [JsonPropertyName("seed-queue-size")]
    public int SeedQueueSize { get; set; }

    [JsonPropertyName("seed-queue-enabled")]
    public bool SeedQueueEnabled { get; set; }

    [JsonPropertyName("max-peers-per-torrent")]
    public int MaxPeersPerTorrent { get; set; }

    [JsonPropertyName("rpc-version")]
    public int RpcVersion { get; set; }

    [JsonPropertyName("rpc-version-minimum")]
    public int RpcVersionMinimum { get; set; }
}

public class TransmissionSessionStats
{
    [JsonPropertyName("activeTorrentCount")]
    public int ActiveTorrentCount { get; set; }

    [JsonPropertyName("downloadSpeed")]
    public long DownloadSpeed { get; set; }

    [JsonPropertyName("uploadSpeed")]
    public long UploadSpeed { get; set; }

    [JsonPropertyName("pausedTorrentCount")]
    public int PausedTorrentCount { get; set; }

    [JsonPropertyName("torrentCount")]
    public int TorrentCount { get; set; }

    [JsonPropertyName("cumulative-stats")]
    public TransmissionStats? CumulativeStats { get; set; }

    [JsonPropertyName("current-stats")]
    public TransmissionStats? CurrentStats { get; set; }
}

public class TransmissionStats
{
    [JsonPropertyName("downloadedBytes")]
    public long DownloadedBytes { get; set; }

    [JsonPropertyName("uploadedBytes")]
    public long UploadedBytes { get; set; }

    [JsonPropertyName("filesAdded")]
    public long FilesAdded { get; set; }

    [JsonPropertyName("sessionCount")]
    public long SessionCount { get; set; }

    [JsonPropertyName("secondsActive")]
    public long SecondsActive { get; set; }
}
