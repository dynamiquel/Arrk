using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Clients;

public class ArrClient
{
    /// <summary>
    /// Default per-request timeout for arr API calls. This is plenty for Radarr, but Sonarr's
    /// series lookup proxies TVDB and is intermittently slower, so <see cref="SonarrClient"/>
    /// raises its own timeout above this base value.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    protected static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Wire format for arr writes (camelCase, like the arrs' own resources).</summary>
    protected static readonly JsonSerializerOptions JsonWriteOpts = new(JsonOpts)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    protected readonly HttpClient Http;
    readonly string _apiKey;

    /// <summary>The per-request HTTP timeout currently configured on this client.</summary>
    public TimeSpan RequestTimeout { get; }

    public ArrClient(string url, string apiKey)
        : this(url, apiKey, null, DefaultRequestTimeout)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets callers inject an <see cref="HttpMessageHandler"/>
    /// (for example a fake transport) while still exercising the real client request/response path.
    /// </summary>
    public ArrClient(string url, string apiKey, HttpMessageHandler? handler)
        : this(url, apiKey, handler, DefaultRequestTimeout)
    {
    }

    /// <summary>
    /// Full constructor that also sets the per-request timeout. Callers that wrap a slower backend
    /// (e.g. Sonarr's TVDB-backed series lookup) pass a larger <paramref name="requestTimeout"/>.
    /// </summary>
    public ArrClient(string url, string apiKey, HttpMessageHandler? handler, TimeSpan requestTimeout)
    {
        _apiKey = apiKey;
        Http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        // Ensure a trailing slash so relative request paths keep the urlbase (e.g. "/radarr").
        Http.BaseAddress = new(url.EndsWith('/') ? url : url + "/");
        RequestTimeout = requestTimeout;
        Http.Timeout = requestTimeout;
    }

    protected HttpRequestMessage CreateRequest(string path)
    {
        HttpRequestMessage request = new(HttpMethod.Get, Relative(path));
        request.Headers.Add("X-Api-Key", _apiKey);
        return request;
    }

    protected HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        HttpRequestMessage request = new(method, Relative(path));
        request.Headers.Add("X-Api-Key", _apiKey);
        return request;
    }

    /// <summary>
    /// Arr configures a urlbase (e.g. "/radarr", "/sonarr") and Stackarr's client base address
    /// includes it. A leading slash on the request path would discard that urlbase, so strip it
    /// and let it resolve relative to the base address instead.
    /// </summary>
    static string Relative(string path) => path.StartsWith('/') ? path[1..] : path;

    public virtual async Task<List<QualityProfile>> GetQualityProfiles(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/qualityprofile");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<QualityProfile>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<QualityProfile> CreateQualityProfile(QualityProfile profile, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/qualityprofile");
        request.Content = JsonContent.Create(profile);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<QualityProfile>(json, JsonOpts)!;
    }

    public virtual async Task<QualityProfile> UpdateQualityProfile(QualityProfile profile, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, $"/api/v3/qualityprofile/{profile.Id}");
        request.Content = JsonContent.Create(profile);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<QualityProfile>(json, JsonOpts)!;
    }

    public virtual async Task DeleteQualityProfile(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v3/qualityprofile/{id}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<RootFolder>> GetRootFolders(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/rootfolder");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<RootFolder>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<RootFolder> AddRootFolder(string path, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/rootfolder");
        request.Content = JsonContent.Create(new { path, accessible = true });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<RootFolder>(json, JsonOpts)!;
    }

    public virtual async Task DeleteRootFolder(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v3/rootfolder/{id}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<CustomFormatDto>> GetCustomFormats(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/customformat");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<CustomFormatDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<CustomFormatDto> CreateCustomFormat(object payload, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/customformat");
        request.Content = JsonContent.Create(payload);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CustomFormatDto>(json, JsonOpts)!;
    }

    public virtual async Task<List<QualityDefinitionDto>> GetQualityDefinitions(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/qualitydefinition");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<QualityDefinitionDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task UpdateQualityDefinition(QualityDefinitionDto definition, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, $"/api/v3/qualitydefinition/{definition.Id}");
        request.Content = JsonContent.Create(definition);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<TagDto>> GetTags(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/tag");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<TagDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<SystemStatus> GetSystemStatus(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/system/status");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<SystemStatus>(json, JsonOpts)!;
    }

    public virtual async Task<List<QueueItemDto>> GetQueue(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/queue");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<QueueItemDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<List<DownloadClientDto>> GetDownloadClients(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/downloadclient");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<DownloadClientDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<DownloadClientDto> CreateDownloadClient(object payload, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/downloadclient");
        request.Content = JsonContent.Create(payload, options: JsonOpts);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<DownloadClientDto>(json, JsonOpts)!;
    }

    public virtual async Task DeleteDownloadClient(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v3/downloadclient/{id}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Reads the arr's download-handling config (the "Download Handling" block under Download
    /// Clients). Returned as a mutable JSON object so a partial update can be written back without
    /// dropping fields this Stackarr version does not model (Radarr adds extra keys Sonarr omits).
    /// </summary>
    public virtual async Task<JsonObject> GetDownloadClientConfig(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/config/downloadclient");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Download client config response was not a JSON object");
    }

    /// <summary>Writes back a previously read download-handling config (whole-resource PUT).</summary>
    public virtual async Task UpdateDownloadClientConfig(JsonObject config, CancellationToken ct = default)
    {
        int id = config["id"] is JsonValue value && value.TryGetValue(out int parsed) ? parsed : 1;
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, $"/api/v3/config/downloadclient/{id}");
        request.Content = JsonContent.Create(config);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    protected async Task<List<HistoryDto>> GetHistory(string queryParam, int entityId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/history?{queryParam}={entityId}&pageSize=1000");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);

        // The arr history endpoint returns a paged resource ({ page, pageSize, totalRecords, records }).
        QueuePagingResource<HistoryDto>? paged =
            JsonSerializer.Deserialize<QueuePagingResource<HistoryDto>>(json, JsonOpts);
        return paged?.Records ?? [];
    }

    /// <summary>
    /// Finds history records for a torrent hash. Unlike the live queue, history persists after the
    /// download is gone from the client, so it is the reliable way to map a removed torrent back to
    /// the movie/series it came from.
    /// </summary>
    /// <remarks>
    /// The arrs store torrent infohashes in upper case while Transmission reports them in lower
    /// case, and the <c>downloadId</c> filter is case-sensitive. Query the canonical upper-case
    /// form first and fall back to the caller's spelling for any non-hash download id, otherwise a
    /// failed download whose queue item has already disappeared can never be traced back.
    /// </remarks>
    public virtual async Task<List<HistoryDto>> GetHistoryByDownloadId(string downloadId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(downloadId))
            return [];

        string canonical = CanonicalDownloadId(downloadId);
        List<HistoryDto> records = await GetHistoryByDownloadIdCore(canonical, ct);
        if (records.Count == 0 && !string.Equals(canonical, downloadId, StringComparison.Ordinal))
            records = await GetHistoryByDownloadIdCore(downloadId, ct);

        return records;
    }

    static string CanonicalDownloadId(string downloadId) =>
        downloadId.All(Uri.IsHexDigit) ? downloadId.ToUpperInvariant() : downloadId;

    async Task<List<HistoryDto>> GetHistoryByDownloadIdCore(string downloadId, CancellationToken ct)
    {
        HttpRequestMessage request = CreateRequest(
            $"/api/v3/history?downloadId={Uri.EscapeDataString(downloadId)}&pageSize=100");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);

        QueuePagingResource<HistoryDto>? paged =
            JsonSerializer.Deserialize<QueuePagingResource<HistoryDto>>(json, JsonOpts);
        return paged?.Records ?? [];
    }

    /// <summary>
    /// Recent history across every entity, newest first. Used to detect completed imports: unlike
    /// the live queue, history persists after the download has been removed from the client.
    /// </summary>
    public virtual async Task<List<HistoryDto>> GetRecentHistory(int pageSize = 200, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(
            $"/api/v3/history?pageSize={pageSize}&sortKey=date&sortDirection=descending");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);

        QueuePagingResource<HistoryDto>? paged =
            JsonSerializer.Deserialize<QueuePagingResource<HistoryDto>>(json, JsonOpts);
        return paged?.Records ?? [];
    }

    /// <summary>
    /// Marks a grabbed download as failed. This is the arr's own failure handling: it blocklists
    /// the release so the same release is not grabbed again.
    /// </summary>
    public virtual async Task MarkHistoryFailed(int historyId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, $"/api/v3/history/failed/{historyId}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Removes a queue item, optionally blocklisting the release and triggering a fresh search.
    /// Stackarr removes the torrent from the client itself first, so the default is to leave the
    /// client alone (<paramref name="removeFromClient"/> false); a failure is retried without the
    /// client removal as a fallback for arr versions that reject it.
    /// </summary>
    protected async Task DeleteQueueItemCore(
        int queueItemId,
        bool blocklist,
        bool removeFromClient,
        bool searchAgain,
        CancellationToken ct)
    {
        async Task<HttpResponseMessage> Send(bool remove)
        {
            string query =
                $"?removeFromClient={remove.ToString().ToLowerInvariant()}" +
                $"&blocklist={blocklist.ToString().ToLowerInvariant()}" +
                $"&skipRedownload={(!searchAgain).ToString().ToLowerInvariant()}";
            HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v3/queue/{queueItemId}{query}");
            return await Http.SendAsync(request, ct);
        }

        HttpResponseMessage response = await Send(removeFromClient);

        if (!response.IsSuccessStatusCode
            && response.StatusCode != System.Net.HttpStatusCode.NotFound
            && removeFromClient)
        {
            response = await Send(false);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Asks the arr what it can see in a folder. Both arrs share this endpoint shape; Radarr fills
    /// <c>movie</c> and Sonarr fills <c>series</c>/<c>episodes</c>. The folder must be a path the
    /// arr container can read (Stackarr stages uploads under its <c>/downloads</c> mount).
    /// </summary>
    public virtual async Task<List<ManualImportResourceDto>> GetManualImport(
        string folder,
        bool filterExistingFiles = false,
        CancellationToken ct = default)
    {
        string query = $"folder={Uri.EscapeDataString(folder)}&filterExistingFiles={filterExistingFiles.ToString().ToLowerInvariant()}";
        HttpRequestMessage request = CreateRequest($"/api/v3/manualimport?{query}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ManualImportResourceDto>>(json, JsonOpts) ?? [];
    }

    /// <summary>
    /// Re-runs the arr's parser with an explicit target, so quality, languages and rejections are
    /// calculated for the title/episode we are about to import rather than whatever the file name
    /// happened to match.
    /// </summary>
    public virtual async Task<List<ManualImportResourceDto>> ReprocessManualImport(
        IReadOnlyList<object> items,
        CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/manualimport");
        request.Content = JsonContent.Create(items, options: JsonWriteOpts);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ManualImportResourceDto>>(json, JsonOpts) ?? [];
    }

    /// <summary>Submits a manual import command and returns the arr command resource to poll.</summary>
    public virtual async Task<ArrCommandDto> SendManualImport(
        IReadOnlyList<ManualImportFileDto> files,
        string importMode,
        CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/command");
        request.Content = JsonContent.Create(new
        {
            name = "ManualImport",
            importMode,
            files
        }, options: JsonWriteOpts);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ArrCommandDto>(json, JsonOpts)!;
    }

    public virtual async Task<ArrCommandDto?> GetCommand(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/command/{id}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ArrCommandDto>(json, JsonOpts);
    }
}
