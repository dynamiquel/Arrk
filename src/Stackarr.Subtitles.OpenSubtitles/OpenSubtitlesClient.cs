using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Stackarr.Core.Modules;

namespace Stackarr.Subtitles.OpenSubtitles;

/// <summary>
/// Typed client for the OpenSubtitles.com REST API. The API key is user supplied; username and
/// password are optional and only raise the account's daily download quota.
/// </summary>
public sealed class OpenSubtitlesClient
{
    public const string BaseUrl = "https://api.opensubtitles.com/api/v1/";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(250);

    readonly HttpClient _http;
    readonly IOptionsMonitor<OpenSubtitlesConfig> _config;
    readonly SemaphoreSlim _requestGate = new(1, 1);
    readonly SemaphoreSlim _tokenGate = new(1, 1);

    DateTimeOffset _lastRequest;
    string? _token;
    DateTimeOffset _tokenExpiresAt;

    public OpenSubtitlesClient(
        IOptionsMonitor<OpenSubtitlesConfig> config,
        HttpMessageHandler? handler = null)
    {
        _config = config;
        _http = handler is null ? new HttpClient() : new(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>Downloads left today as reported by the last download response, when known.</summary>
    public int? RemainingDownloads { get; private set; }

    public string? ApiKey => _config.CurrentValue.ApiKey?.Trim();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return false;

        try
        {
            await GetLanguagesAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<OpenSubtitlesLanguage>> GetLanguagesAsync(CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "infos/languages");
        using HttpResponseMessage response = await SendAsync(request, authenticated: false, ct);
        await EnsureSuccessAsync(response, ct);

        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out JsonElement data))
        {
            root = data;
        }

        if (root.ValueKind != JsonValueKind.Array)
            return [];

        return JsonSerializer.Deserialize<List<OpenSubtitlesLanguage>>(root.GetRawText(), JsonOptions) ?? [];
    }

    public async Task<IReadOnlyList<OpenSubtitlesSubtitle>> SearchAsync(
        OpenSubtitlesSearchQuery query,
        CancellationToken ct = default)
    {
        string path = "subtitles?" + BuildQuery(query);
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        using HttpResponseMessage response = await SendAsync(request, authenticated: false, ct);
        await EnsureSuccessAsync(response, ct);

        OpenSubtitlesSearchResponse? payload =
            await response.Content.ReadFromJsonAsync<OpenSubtitlesSearchResponse>(JsonOptions, ct);
        return payload?.Data ?? [];
    }

    public async Task<OpenSubtitlesDownloadResponse> DownloadAsync(
        int fileId,
        string? subFormat = "srt",
        CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "download")
        {
            Content = JsonContent.Create(new
            {
                file_id = fileId,
                sub_format = subFormat
            }, options: JsonOptions)
        };

        using HttpResponseMessage response = await SendAsync(request, authenticated: true, ct);
        await EnsureSuccessAsync(response, ct);

        OpenSubtitlesDownloadResponse? payload =
            await response.Content.ReadFromJsonAsync<OpenSubtitlesDownloadResponse>(JsonOptions, ct);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Link))
            throw new OpenSubtitlesException((int)response.StatusCode, "OpenSubtitles returned no download link.");

        RemainingDownloads = payload.Remaining;
        return payload;
    }

    public async Task<byte[]> GetFileAsync(string link, CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, link);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new OpenSubtitlesException((int)response.StatusCode, "Could not download the subtitle file.");

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    static string UserAgent => $"Stackarr/{StackarrVersion.Current}";

    static string BuildQuery(OpenSubtitlesSearchQuery query)
    {
        List<string> parts = [];
        Add(parts, "imdb_id", query.ImdbId);
        Add(parts, "tmdb_id", query.TmdbId?.ToString(CultureInfo.InvariantCulture));
        Add(parts, "parent_imdb_id", query.ParentImdbId);
        Add(parts, "parent_tmdb_id", query.ParentTmdbId?.ToString(CultureInfo.InvariantCulture));
        Add(parts, "query", query.Query);
        Add(parts, "season_number", query.SeasonNumber?.ToString(CultureInfo.InvariantCulture));
        Add(parts, "episode_number", query.EpisodeNumber?.ToString(CultureInfo.InvariantCulture));
        Add(parts, "moviehash", query.MovieHash);
        Add(parts, "year", query.Year?.ToString(CultureInfo.InvariantCulture));
        Add(parts, "type", query.Type);
        if (query.Languages.Count > 0)
            Add(parts, "languages", string.Join(',', query.Languages));
        Add(parts, "hearing_impaired", "include");
        Add(parts, "foreign_parts_only", "include");
        Add(parts, "machine_translated", query.IncludeMachineTranslated ? "include" : "exclude");
        Add(parts, "ai_translated", query.IncludeMachineTranslated ? "include" : "exclude");
        return string.Join('&', parts);
    }

    static void Add(List<string> parts, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add($"{key}={Uri.EscapeDataString(value)}");
    }

    async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        bool authenticated,
        CancellationToken ct)
    {
        string apiKey = ApiKey ?? "";
        if (apiKey.Length == 0)
            throw new OpenSubtitlesException(401, "OpenSubtitles API key is not configured.");

        string? token = authenticated ? await GetTokenAsync(ct) : null;
        HttpResponseMessage response = await SendCoreAsync(request, apiKey, token, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TimeSpan delay = RetryDelay(response);
            response.Dispose();
            await Task.Delay(delay, ct);
            using HttpRequestMessage rateLimitRetry = Clone(request);
            response = await SendCoreAsync(rateLimitRetry, apiKey, token, ct);
        }

        if (response.StatusCode != HttpStatusCode.Unauthorized || !authenticated)
            return response;

        response.Dispose();
        _token = null;
        _tokenExpiresAt = default;
        token = await GetTokenAsync(ct, force: true);

        using HttpRequestMessage retry = Clone(request);
        return await SendCoreAsync(retry, apiKey, token, ct);
    }

    async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request,
        string apiKey,
        string? token,
        CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation("Api-Key", apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await ThrottleAsync(ct);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    async Task ThrottleAsync(CancellationToken ct)
    {
        await _requestGate.WaitAsync(ct);
        try
        {
            TimeSpan wait = MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    async Task<string?> GetTokenAsync(CancellationToken ct, bool force = false)
    {
        OpenSubtitlesConfig config = _config.CurrentValue;
        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
            return null;

        if (!force && _token is not null && _tokenExpiresAt > DateTimeOffset.UtcNow)
            return _token;

        await _tokenGate.WaitAsync(ct);
        try
        {
            if (!force && _token is not null && _tokenExpiresAt > DateTimeOffset.UtcNow)
                return _token;

            using HttpRequestMessage request = new(HttpMethod.Post, "login")
            {
                Content = JsonContent.Create(new
                {
                    username = config.Username,
                    password = config.Password
                }, options: JsonOptions)
            };
            request.Headers.TryAddWithoutValidation("Api-Key", ApiKey ?? "");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            await ThrottleAsync(ct);
            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            await EnsureSuccessAsync(response, ct);

            OpenSubtitlesLoginResponse? login =
                await response.Content.ReadFromJsonAsync<OpenSubtitlesLoginResponse>(JsonOptions, ct);
            if (string.IsNullOrWhiteSpace(login?.Token))
                return null;

            _token = login.Token;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddHours(23);
            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    static TimeSpan RetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            return delta > TimeSpan.Zero && delta < TimeSpan.FromSeconds(30) ? delta : TimeSpan.FromSeconds(2);

        if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(30))
                return wait;
        }

        return TimeSpan.FromSeconds(2);
    }

    static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        HttpRequestMessage clone = new(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            string body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return clone;
    }

    static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body = await response.Content.ReadAsStringAsync(ct);
        string message = "";
        try
        {
            OpenSubtitlesErrorResponse? error =
                JsonSerializer.Deserialize<OpenSubtitlesErrorResponse>(body, JsonOptions);
            message = error?.Message ?? error?.Error ?? "";
        }
        catch (JsonException)
        {
            // Fall through to the generic message.
        }

        if (string.IsNullOrWhiteSpace(message))
            message = string.IsNullOrWhiteSpace(body)
                ? $"OpenSubtitles request failed with status {(int)response.StatusCode}."
                : body.Trim();

        throw new OpenSubtitlesException((int)response.StatusCode, message);
    }
}

/// <summary>A search across the OpenSubtitles catalogue.</summary>
public sealed record OpenSubtitlesSearchQuery
{
    public string? ImdbId { get; init; }

    public int? TmdbId { get; init; }

    public string? ParentImdbId { get; init; }

    public int? ParentTmdbId { get; init; }

    public string? Query { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public string? MovieHash { get; init; }

    public int? Year { get; init; }

    public string Type { get; init; } = "all";

    public IReadOnlyList<string> Languages { get; init; } = [];

    public bool IncludeMachineTranslated { get; init; }
}
