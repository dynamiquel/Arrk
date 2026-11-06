using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arrk.Slskd;

/// <summary>
/// Thin client for the slskd REST API (v0). Authenticates with the web username/password
/// (JWT via <c>POST /api/v0/session</c>) so no API key has to be pre-seeded into the container.
/// </summary>
public class SlskdClient
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http;
    readonly Func<string> _baseUrl;
    readonly Func<(string Username, string Password)> _credentials;
    readonly SemaphoreSlim _loginGate = new(1, 1);
    string? _token;
    string? _tokenUsername;

    public SlskdClient(string url, string username, string password)
        : this(() => url, () => (username, password), null)
    {
    }

    public SlskdClient(string url, string username, string password, HttpMessageHandler? handler)
        : this(() => url, () => (username, password), handler)
    {
    }

    /// <summary>
    /// Live-resolving constructor: the URL (which follows the VPN routing toggle) and the
    /// credentials (which follow <c>services.slskd.api_username/api_password</c>) are resolved per
    /// call, so those config edits apply without a restart.
    /// </summary>
    public SlskdClient(Func<string> baseUrl, Func<(string Username, string Password)> credentials, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl;
        _credentials = credentials;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string BaseUrl
    {
        get
        {
            string url = _baseUrl();
            return url.EndsWith('/') ? url.TrimEnd('/') : url;
        }
    }

    async Task<bool> EnsureTokenAsync(CancellationToken ct)
    {
        (string username, string password) = _credentials();

        if (!string.IsNullOrEmpty(_token) && string.Equals(_tokenUsername, username, StringComparison.Ordinal))
            return true;

        await _loginGate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_token) && string.Equals(_tokenUsername, username, StringComparison.Ordinal))
                return true;
            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                $"{BaseUrl}/api/v0/session", new { username, password }, JsonOpts, ct);
            if (!response.IsSuccessStatusCode)
                return false;
            SlskdSession? session = await response.Content.ReadFromJsonAsync<SlskdSession>(JsonOpts, ct);
            _token = session?.Token;
            _tokenUsername = username;
            return !string.IsNullOrEmpty(_token);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string path, CancellationToken ct)
    {
        HttpRequestMessage request = new(method, $"{BaseUrl}{path}");
        request.Headers.Add("Accept", "application/json");
        if (await EnsureTokenAsync(ct))
            request.Headers.Add("Authorization", $"Bearer {_token}");
        return request;
    }

    /// <summary>
    /// Sends an authorised request and discards the cached session token on a 401. slskd invalidates the
    /// JWT when it restarts, so the next call must log in again, otherwise every request stays
    /// unauthorised ("Soulseek unavailable") until the app is restarted.
    /// </summary>
    async Task<HttpResponseMessage> SendTrackedAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            _token = null;
        return response;
    }

    /// <summary>True when slskd is reachable and logged in to the Soulseek network.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpRequestMessage request = await AuthorizedAsync(HttpMethod.Get, "/api/v0/server", ct);
            using HttpResponseMessage response = await SendTrackedAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return false;
            SlskdServer? server = await response.Content.ReadFromJsonAsync<SlskdServer>(JsonOpts, ct);
            return server?.IsLoggedIn == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts a search and returns its id.</summary>
    public async Task<string?> StartSearchAsync(string query, CancellationToken ct = default)
    {
        using HttpRequestMessage request = await AuthorizedAsync(HttpMethod.Post, "/api/v0/searches", ct);
        request.Content = JsonContent.Create(new { searchText = query }, options: JsonOpts);
        using HttpResponseMessage response = await SendTrackedAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        SlskdSearch? search = await response.Content.ReadFromJsonAsync<SlskdSearch>(JsonOpts, ct);
        return search?.Id;
    }

    /// <summary>Fetches a search (with its accumulated file responses).</summary>
    public async Task<SlskdSearch?> GetSearchAsync(string id, CancellationToken ct = default)
    {
        using HttpRequestMessage request = await AuthorizedAsync(
            HttpMethod.Get, $"/api/v0/searches/{Uri.EscapeDataString(id)}?includeResponses=true", ct);
        using HttpResponseMessage response = await SendTrackedAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<SlskdSearch>(JsonOpts, ct);
    }

    public async Task<bool> DeleteSearchAsync(string id, CancellationToken ct = default)
    {
        using HttpRequestMessage request = await AuthorizedAsync(HttpMethod.Delete, $"/api/v0/searches/{id}", ct);
        using HttpResponseMessage response = await SendTrackedAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Enqueues one or more files from a peer.</summary>
    public async Task<bool> EnqueueAsync(string username, IEnumerable<SlskdFile> files, CancellationToken ct = default)
    {
        List<object> payload = files
            .Select(f => (object)new { filename = f.Filename, size = f.Size })
            .ToList();
        if (payload.Count == 0)
            return false;

        using HttpRequestMessage request = await AuthorizedAsync(
            HttpMethod.Post, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}", ct);
        request.Content = JsonContent.Create(payload, options: JsonOpts);
        using HttpResponseMessage response = await SendTrackedAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>All active/completed downloads, flattened per file.</summary>
    public async Task<List<SlskdDownload>> GetDownloadsAsync(CancellationToken ct = default)
    {
        using HttpRequestMessage request = await AuthorizedAsync(HttpMethod.Get, "/api/v0/transfers/downloads", ct);
        using HttpResponseMessage response = await SendTrackedAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return [];
        List<SlskdUserTransfers>? users = await response.Content.ReadFromJsonAsync<List<SlskdUserTransfers>>(JsonOpts, ct);
        if (users is null)
            return [];

        List<SlskdDownload> downloads = [];
        foreach (SlskdUserTransfers user in users)
        {
            foreach (SlskdDirectory dir in user.Directories ?? [])
            {
                foreach (SlskdFileTransfer file in dir.Files ?? [])
                {
                    downloads.Add(new SlskdDownload
                    {
                        Id = file.Id ?? "",
                        Username = user.Username ?? file.Username ?? "",
                        Directory = dir.Directory ?? "",
                        Filename = file.Filename ?? "",
                        State = file.State ?? "",
                        PercentComplete = file.PercentComplete,
                        Size = file.Size ?? 0,
                        BytesTransferred = file.BytesTransferred ?? 0,
                        EnqueuedAt = file.EnqueuedAt ?? file.RequestedAt,
                        StartedAt = file.StartedAt,
                        EndedAt = file.EndedAt,
                        Exception = file.Exception ?? ""
                    });
                }
            }
        }
        return downloads;
    }

    /// <summary>
    /// Cancels one queued/active download (and optionally deletes its partial file). Best-effort:
    /// slskd returns 200/204 on success and the caller treats any failure as non-fatal.
    /// </summary>
    public async Task<bool> CancelDownloadAsync(string username, string id, bool remove = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(id))
            return false;

        string path = $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(id)}"
            + (remove ? "?remove=true" : "");
        using HttpRequestMessage request = await AuthorizedAsync(HttpMethod.Delete, path, ct);
        using HttpResponseMessage response = await SendTrackedAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Applies the global transfer speed limits at run time (KiB/s; &lt;= 0 = unlimited) via slskd's
    /// options overlay. Best-effort: slot limits require a restart and are set through the container
    /// environment, so a slskd that rejects the overlay is non-fatal.
    /// </summary>
    public async Task<bool> SetTransferLimitsAsync(int uploadSpeedKib, int downloadSpeedKib, CancellationToken ct = default)
    {
        object overlay = new
        {
            transfers = new
            {
                upload = new { speedLimit = uploadSpeedKib > 0 ? uploadSpeedKib : int.MaxValue },
                download = new { speedLimit = downloadSpeedKib > 0 ? downloadSpeedKib : int.MaxValue }
            }
        };
        using HttpRequestMessage request = await AuthorizedAsync(HttpMethod.Patch, "/api/v0/options", ct);
        request.Content = JsonContent.Create(overlay, options: JsonOpts);
        using HttpResponseMessage response = await SendTrackedAsync(request, ct);
        return response.IsSuccessStatusCode;
    }
}

// DTOs (tolerant; slskd v0 shapes)

public class SlskdSession
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

public class SlskdServer
{
    [JsonPropertyName("isLoggedIn")] public bool IsLoggedIn { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
}

public class SlskdSearch
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("isComplete")] public bool IsComplete { get; set; }
    [JsonPropertyName("responses")] public List<SlskdResponse> Responses { get; set; } = [];
}

public class SlskdResponse
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("hasFreeUploadSlot")] public bool HasFreeUploadSlot { get; set; }
    [JsonPropertyName("queueLength")] public int QueueLength { get; set; }
    [JsonPropertyName("uploadSpeed")] public int UploadSpeed { get; set; }
    [JsonPropertyName("files")] public List<SlskdFile> Files { get; set; } = [];
}

public class SlskdFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("bitRate")] public int? BitRate { get; set; }
    [JsonPropertyName("sampleRate")] public int? SampleRate { get; set; }
    [JsonPropertyName("bitDepth")] public int? BitDepth { get; set; }
    [JsonPropertyName("length")] public int? Length { get; set; }
}

public class SlskdUserTransfers
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("directories")] public List<SlskdDirectory>? Directories { get; set; }
}

public class SlskdDirectory
{
    [JsonPropertyName("directory")] public string? Directory { get; set; }
    [JsonPropertyName("files")] public List<SlskdFileTransfer>? Files { get; set; }
}

public class SlskdFileTransfer
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("percentComplete")] public double PercentComplete { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("bytesTransferred")] public long? BytesTransferred { get; set; }
    [JsonPropertyName("requestedAt")] public DateTime? RequestedAt { get; set; }
    [JsonPropertyName("enqueuedAt")] public DateTime? EnqueuedAt { get; set; }
    [JsonPropertyName("startedAt")] public DateTime? StartedAt { get; set; }
    [JsonPropertyName("endedAt")] public DateTime? EndedAt { get; set; }
    [JsonPropertyName("exception")] public string? Exception { get; set; }
}

/// <summary>A single completed/active Soulseek file (flattened from the downloads tree).</summary>
public record SlskdDownload
{
    public string Id { get; init; } = "";
    public string Username { get; init; } = "";
    /// <summary>The remote directory the file belongs to (slskd's <c>SOURCE_DIRECTORY</c>).</summary>
    public string Directory { get; init; } = "";
    public string Filename { get; init; } = "";
    public string State { get; init; } = "";
    public double PercentComplete { get; init; }
    public long Size { get; init; }
    public long BytesTransferred { get; init; }
    public DateTime? EnqueuedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public string Exception { get; init; } = "";

    public bool IsComplete => string.Equals(State, "Completed, Succeeded", StringComparison.OrdinalIgnoreCase)
        || PercentComplete >= 100;

    /// <summary>True when the transfer failed and won't make progress on its own.</summary>
    public bool IsFailed => !string.IsNullOrEmpty(Exception)
        || State.Contains("Error", StringComparison.OrdinalIgnoreCase)
        || State.Contains("Cancel", StringComparison.OrdinalIgnoreCase)
        || State.Contains("Abort", StringComparison.OrdinalIgnoreCase)
        || State.Contains("Reject", StringComparison.OrdinalIgnoreCase)
        || State.Contains("Fail", StringComparison.OrdinalIgnoreCase)
        || State.Contains("TimedOut", StringComparison.OrdinalIgnoreCase)
        || State.Contains("Timed Out", StringComparison.OrdinalIgnoreCase);

    /// <summary>True while slskd has the file queued at the peer but hasn't started transferring.</summary>
    public bool IsQueued => !StartedAt.HasValue && !IsComplete && !IsFailed;

    /// <summary>The file's base name (used to match a download to the album grab that requested it).</summary>
    public string FileName => Path.GetFileName(Filename.Replace('\\', '/'));

    /// <summary>The leaf remote directory (slskd saves downloads under it), used as the grab key.</summary>
    public string DirectoryName
    {
        get
        {
            string dir = !string.IsNullOrWhiteSpace(Directory)
                ? Directory
                : (Path.GetDirectoryName(Filename.Replace('\\', '/')) ?? "");
            return Path.GetFileName(dir.Replace('\\', '/').TrimEnd('/'));
        }
    }
}
