using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stackarr.Prowlarr;

public class ProwlarrClient
{
    public static readonly JsonSerializerOptions JsonOpts = JsonSerializerOptions.Web;

    protected readonly HttpClient _http;
    readonly string _apiKey;

    public ProwlarrClient(string url, string apiKey)
        : this(url, apiKey, null)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets callers inject an <see cref="HttpMessageHandler"/>
    /// while still exercising the real client request/response path.
    /// </summary>
    public ProwlarrClient(string url, string apiKey, HttpMessageHandler? handler)
    {
        _apiKey = apiKey;
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        // Trailing slash keeps any configured urlbase (e.g. "/prowlarr") when resolving paths.
        _http.BaseAddress = new(url.EndsWith('/') ? url : url + "/");
        // Indexer searches fan out to every tracker and can take well over a minute (The Pirate Bay
        // is especially slow), so a short timeout silently drops grabs. Allow generous headroom.
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    static string Relative(string path) => path.StartsWith('/') ? path[1..] : path;

    public virtual async Task<List<ProwlarrIndexer>> GetIndexers(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v1/indexer");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ProwlarrIndexer>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<ProwlarrIndexer> CreateIndexer(ProwlarrIndexer indexer, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v1/indexer");
        request.Content = JsonContent.Create(indexer, options: JsonOpts);
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProwlarrIndexer>(json, JsonOpts)!;
    }

    public virtual async Task<ProwlarrIndexer> UpdateIndexer(ProwlarrIndexer indexer, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, $"/api/v1/indexer/{indexer.Id}");
        request.Content = JsonContent.Create(indexer, options: JsonOpts);
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProwlarrIndexer>(json, JsonOpts)!;
    }

    public virtual async Task DeleteIndexer(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v1/indexer/{id}");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<ProwlarrIndexerDefinition>> GetIndexerDefinitions(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v1/indexer/schema");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ProwlarrIndexerDefinition>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<List<ProwlarrApplication>> GetApplications(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v1/applications");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ProwlarrApplication>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<ProwlarrApplication> CreateApplication(ProwlarrApplication app, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v1/applications");
        request.Content = JsonContent.Create(app, options: JsonOpts);
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProwlarrApplication>(json, JsonOpts)!;
    }

    public virtual async Task<ProwlarrApplication> UpdateApplication(ProwlarrApplication app, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, $"/api/v1/applications/{app.Id}");
        request.Content = JsonContent.Create(app, options: JsonOpts);
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProwlarrApplication>(json, JsonOpts)!;
    }

    public virtual async Task DeleteApplication(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v1/applications/{id}");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Searches Prowlarr (which fans out to all its indexers). <paramref name="categories"/> and
    /// <paramref name="indexerIds"/> are optional filters; omitting them searches everything.
    /// Music discovery passes the audio categories (<c>3000-3999</c>) so TV/game/console releases
    /// don't drown out the soundtrack results (The Pirate Bay treats the query as an AND).
    /// </summary>
    public virtual async Task<List<ProwlarrSearchResult>> Search(
        string query,
        IEnumerable<int>? categories = null,
        IEnumerable<int>? indexerIds = null,
        CancellationToken ct = default)
    {
        List<string> parts = [$"query={Uri.EscapeDataString(query)}", "type=search"];
        // Prowlarr expects each category/indexer as its own repeated query parameter, a
        // comma-separated list is rejected with a 400.
        if (categories is not null)
            foreach (int category in categories)
                parts.Add($"categories={category}");
        if (indexerIds is not null)
            foreach (int indexerId in indexerIds)
                parts.Add($"indexerIds={indexerId}");

        HttpRequestMessage request = CreateRequest($"/api/v1/search?{string.Join("&", parts)}");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ProwlarrSearchResult>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<ProwlarrSystemStatus> GetSystemStatus(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v1/system/status");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProwlarrSystemStatus>(json, JsonOpts)!;
    }

    public virtual async Task<ProwlarrIndexerStats> GetIndexerStats(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v1/indexerstats");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProwlarrIndexerStats>(json, JsonOpts)!;
    }

    public virtual async Task<List<ProwlarrAppProfile>> GetAppProfiles(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v1/appprofile");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ProwlarrAppProfile>>(json, JsonOpts) ?? [];
    }

    // Indexer proxies (FlareSolverr / Byparr, HTTP, SOCKS)

    public virtual async Task<List<ProwlarrIndexerProxy>> GetIndexerProxies(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v1/indexerproxy");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ProwlarrIndexerProxy>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<ProwlarrIndexerProxy> CreateIndexerProxy(ProwlarrIndexerProxy proxy, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v1/indexerproxy");
        request.Content = JsonContent.Create(proxy, options: JsonOpts);
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProwlarrIndexerProxy>(json, JsonOpts)!;
    }

    public virtual async Task<ProwlarrIndexerProxy> UpdateIndexerProxy(ProwlarrIndexerProxy proxy, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, $"/api/v1/indexerproxy/{proxy.Id}");
        request.Content = JsonContent.Create(proxy, options: JsonOpts);
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProwlarrIndexerProxy>(json, JsonOpts)!;
    }

    public virtual async Task DeleteIndexerProxy(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v1/indexerproxy/{id}");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    // Tags (link indexers to proxies)

    public virtual async Task<List<ProwlarrTag>> GetTags(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v1/tag");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ProwlarrTag>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<ProwlarrTag> CreateTag(string label, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v1/tag");
        request.Content = JsonContent.Create(new ProwlarrTag { Label = label }, options: JsonOpts);
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ProwlarrTag>(json, JsonOpts)!;
    }

    public virtual async Task DeleteTag(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v1/tag/{id}");
        HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    HttpRequestMessage CreateRequest(string path)
    {
        HttpRequestMessage request = new(HttpMethod.Get, Relative(path));
        request.Headers.Add("X-Api-Key", _apiKey);
        return request;
    }

    HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        HttpRequestMessage request = new(method, Relative(path));
        request.Headers.Add("X-Api-Key", _apiKey);
        return request;
    }
}

public class ProwlarrSystemStatus
{
    public string Version { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string StartupPath { get; set; } = "";
    public string AppData { get; set; } = "";
    public bool IsDebug { get; set; }
    public bool IsProduction { get; set; }
    public bool IsDocker { get; set; }
    public string Os { get; set; } = "";
    public string RuntimeVersion { get; set; } = "";
}

public class ProwlarrIndexer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Implementation { get; set; } = "";
    public string ConfigContract { get; set; } = "";
    public bool Enable { get; set; } = true;
    public string Protocol { get; set; } = "torrent";
    public int Priority { get; set; } = 25;
    public int AppProfileId { get; set; }
    public int DownloadClientId { get; set; }
    public bool RemoveFromDownloadsAndClient { get; set; }
    public bool EnableRss { get; set; }
    public bool EnableAutomaticSearch { get; set; }
    public bool EnableInteractiveSearch { get; set; }
    public bool SupportsRss { get; set; }
    public bool SupportsSearch { get; set; }
    public List<ProwlarrField> Fields { get; set; } = [];
    public string[] Categories { get; set; } = [];
    public List<int> Tags { get; set; } = [];
    public DateTime Added { get; set; }
    public DateTime LastRssSyncReleaseInfo { get; set; }

    public override bool Equals(object? obj)
    {
        if (obj is not ProwlarrIndexer other)
            return false;

        if (Enable != other.Enable) return false;
        if (Priority != other.Priority) return false;
        if (AppProfileId != other.AppProfileId) return false;
        if (EnableRss != other.EnableRss) return false;
        if (EnableAutomaticSearch != other.EnableAutomaticSearch) return false;
        if (EnableInteractiveSearch != other.EnableInteractiveSearch) return false;
        if (DownloadClientId != other.DownloadClientId) return false;
        if (!string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Implementation, other.Implementation, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(ConfigContract, other.ConfigContract, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Protocol, other.Protocol, StringComparison.OrdinalIgnoreCase)) return false;

        if (Categories.Length != other.Categories.Length ||
            !Categories.SequenceEqual(other.Categories, StringComparer.OrdinalIgnoreCase))
            return false;

        if (!Tags.OrderBy(t => t).SequenceEqual(other.Tags.OrderBy(t => t)))
            return false;

        Dictionary<string, object?> thisFields = Fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object?> otherFields = other.Fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.OrdinalIgnoreCase);

        if (thisFields.Count != otherFields.Count) return false;

        foreach (KeyValuePair<string, object?> kvp in otherFields)
        {
            if (!thisFields.TryGetValue(kvp.Key, out object? thisValue))
                return false;

            if (thisValue?.ToString() != kvp.Value?.ToString())
                return false;
        }

        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Name, Implementation);
}

public class ProwlarrApplication
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Implementation { get; set; } = "";
    public string ConfigContract { get; set; } = "";
    public bool Enable { get; set; } = true;
    public string SyncLevel { get; set; } = "addOnly";
    public List<ProwlarrField> Fields { get; set; } = [];
}

public class ProwlarrField
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "textbox";
    [JsonPropertyName("value")]
    public object? Value { get; set; }
    public bool Required { get; set; }
    public string? Placeholder { get; set; }
    public string? HelpText { get; set; }
    public bool Advanced { get; set; }
    public List<ProwlarrSelectOption>? SelectOptions { get; set; }
}

public class ProwlarrSelectOption
{
    public object Value { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ProwlarrIndexerDefinition
{
    public string Implementation { get; set; } = "";
    public string ImplementationName { get; set; } = "";
    public string ConfigContract { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; }
    public string Protocol { get; set; } = "";
    public string[] Categories { get; set; } = [];
    public bool SupportsRss { get; set; }
    public bool SupportsSearch { get; set; }
    public List<ProwlarrField> Fields { get; set; } = [];

    /// <summary>
    /// Extracted from fields[0].value, the Cardigann site identifier (e.g. "thepiratebay", "nyaasi").
    /// Used to match config entries against definitions.
    /// </summary>
    public string DefinitionFile => Fields.FirstOrDefault()?.Value?.ToString() ?? "";
}

public class ProwlarrSearchResult
{
    public string Title { get; set; } = "";
    /// <summary>The release's own link (for torrents this is the magnet URI, otherwise the download URL).</summary>
    public string Guid { get; set; } = "";
    // Sizes can exceed int (some indexers report >2 GB torrents), so keep 64-bit.
    public long Size { get; set; }
    public int Seeders { get; set; }
    public int Peers { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string Indexer { get; set; } = "";
    public string InfoUrl { get; set; } = "";
    public string DownloadClient { get; set; } = "";
    public string MagnetUrl { get; set; } = "";
    public string InfoHash { get; set; } = "";
    [JsonConverter(typeof(ProwlarrFlexibleDateConverter))]
    public DateTime? PublishDate { get; set; }
    public List<ProwlarrCategory> Categories { get; set; } = [];
    public string Protocol { get; set; } = "";
    public bool? Approved { get; set; }
    public bool? Rejected { get; set; }
    public string[] RejectReasons { get; set; } = [];
    [JsonConverter(typeof(ProwlarrFlexibleStringConverter))]
    public string IndexerId { get; set; } = "";
    public string IndexerName { get; set; } = "";
    [JsonConverter(typeof(ProwlarrFlexibleStringConverter))]
    public string DownloadVolumeFactor { get; set; } = "";
    [JsonConverter(typeof(ProwlarrFlexibleStringConverter))]
    public string UploadVolumeFactor { get; set; } = "";
    [JsonConverter(typeof(ProwlarrFlexibleStringConverter))]
    public string ComputedScore { get; set; } = "";
    public string ReleaseHash { get; set; } = "";
    public int? NewsDate { get; set; }
}

/// <summary>A Torznab/Newznab category on a search result (e.g. 3000 "Audio").</summary>
public class ProwlarrCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<ProwlarrCategory> SubCategories { get; set; } = [];
}

public class ProwlarrIndexerStats
{
    public List<ProwlarrIndexerStatsEntry> Indexers { get; set; } = [];
}

public class ProwlarrIndexerStatsEntry
{
    public int IndexerId { get; set; }
    public string IndexerName { get; set; } = "";
    public int NumberOfQueries { get; set; }
    public int NumberOfQueriesFailed { get; set; }
    public int NumberOfGrabs { get; set; }
    public int NumberOfGrabsFailed { get; set; }
    public int NumberOfRssQueries { get; set; }
    public int NumberOfRssQueriesFailed { get; set; }
    public int NumberOfAuthQueries { get; set; }
    public int NumberOfAuthQueriesFailed { get; set; }
    public int NumberOfUserQueries { get; set; }
    public int NumberOfUserQueriesFailed { get; set; }
    public int NumberOfAutomaticQueries { get; set; }
    public int NumberOfAutomaticQueriesFailed { get; set; }
    public int NumberOfInteractiveQueries { get; set; }
    public int NumberOfInteractiveQueriesFailed { get; set; }
    public int NumberOfManualQueries { get; set; }
    public int NumberOfManualQueriesFailed { get; set; }
    public int NumberOfFailedQueries { get; set; }
    public int NumberOfFailedGrabs { get; set; }
    public int NumberOfSuccessfulQueries { get; set; }
    public int NumberOfSuccessfulGrabs { get; set; }
    public int NumberOfResults { get; set; }
    public int NumberOfQueryResults { get; set; }
    public int NumberOfRssResults { get; set; }
    public int NumberOfUserResults { get; set; }
    public int NumberOfAutomaticResults { get; set; }
    public int NumberOfInteractiveResults { get; set; }
    public int NumberOfManualResults { get; set; }
}

public class ProwlarrAppProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// A Prowlarr indexer proxy. The FlareSolverr type is the protocol Byparr implements, so Stackarr
/// registers Byparr under it. A proxy is only used for an indexer that shares one of its tags, and
/// only when Prowlarr detects a Cloudflare challenge.
/// </summary>
public class ProwlarrIndexerProxy
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Implementation { get; set; } = "";
    public string ConfigContract { get; set; } = "";
    public List<ProwlarrField> Fields { get; set; } = [];
    public List<int> Tags { get; set; } = [];
}

/// <summary>A Prowlarr tag. Indexers and proxies reference each other by tag id.</summary>
public class ProwlarrTag
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
/// Prowlarr sends <c>publishDate</c> as an ISO-8601 string, but some versions/endpoints use a Unix
/// timestamp. Accept either (and null) so a single release never fails the whole search.
/// </summary>
public class ProwlarrFlexibleDateConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64()).UtcDateTime,
            JsonTokenType.String => ParseString(reader.GetString()),
            _ => null
        };

    static DateTime? ParseString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (long.TryParse(value, out long unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed)
            ? parsed
            : null;
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToUniversalTime().ToString("o"));
        else
            writer.WriteNullValue();
    }
}

/// <summary>
/// Some Prowlarr fields (notably <c>indexerId</c>, volume factors, computed score) are returned as
/// JSON numbers by the live API but have been strings in other versions. Accept either.
/// </summary>
public class ProwlarrFlexibleStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => "",
            JsonTokenType.String => reader.GetString() ?? "",
            JsonTokenType.Number => reader.TryGetInt64(out long l) ? l.ToString() : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => ""
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
