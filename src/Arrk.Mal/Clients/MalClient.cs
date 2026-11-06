using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Mal.Clients;

/// <summary>
/// Thin client for the MyAnimeList v2 API and its OAuth endpoints. API calls are serialised through
/// a shared rate limiter because MAL publishes no exact limit and punishes bursts with long
/// cooldowns. User tokens never leave the server.
/// </summary>
public sealed class MalClient
{
    public const string ListFields =
        "list_status{status,score,num_episodes_watched,is_rewatching,updated_at,priority,num_times_rewatched,rewatch_value,tags,comments}," +
        "alternative_titles,main_picture,media_type,num_episodes,start_date,end_date,synopsis,mean,rank,genres,studios,nsfw,my_list_status,related_anime";

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly SemaphoreSlim Gate = new(1, 1);
    static DateTime _nextRequestAt = DateTime.MinValue;

    readonly HttpClient _http;
    readonly IOptionsMonitor<MalConfig> _config;
    readonly ILogger<MalClient> _logger;

    public MalClient(
        IOptionsMonitor<MalConfig> config,
        ILogger<MalClient> logger,
        HttpMessageHandler? handler = null)
    {
        _config = config;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    string ApiBase => _config.CurrentValue.ApiBaseUrl.TrimEnd('/');
    string OAuthBase => _config.CurrentValue.OAuthBaseUrl.TrimEnd('/');

    public bool IsConfigured => _config.CurrentValue.IsConfigured;

    public async Task<MalTokenResponse> ExchangeCodeAsync(
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default)
    {
        Dictionary<string, string> form = new()
        {
            ["client_id"] = _config.CurrentValue.ClientId,
            ["client_secret"] = _config.CurrentValue.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        };

        return await SendTokenAsync(form, ct);
    }

    public async Task<MalTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        Dictionary<string, string> form = new()
        {
            ["client_id"] = _config.CurrentValue.ClientId,
            ["client_secret"] = _config.CurrentValue.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };

        return await SendTokenAsync(form, ct);
    }

    async Task<MalTokenResponse> SendTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using FormUrlEncodedContent content = new(form);
        using HttpResponseMessage response = await _http.PostAsync($"{OAuthBase}/token", content, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new MalApiException(response.StatusCode, $"MyAnimeList token request failed: {body}");

        return JsonSerializer.Deserialize<MalTokenResponse>(body, JsonOptions)
            ?? throw new MalApiException(response.StatusCode, "MyAnimeList returned an empty token response.");
    }

    public async Task<MalUser> GetMeAsync(string accessToken, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await SendApiAsync(
            HttpMethod.Get, $"{ApiBase}/users/@me", accessToken, clientId: null, ct);
        return await ReadAsync<MalUser>(response, ct);
    }

    public async Task<MalListPage> GetListPageAsync(string accessToken, string? url, CancellationToken ct = default)
    {
        string endpoint = url ?? $"{ApiBase}/users/@me/animelist?fields={Uri.EscapeDataString(ListFields)}&limit=1000&nsfw=true";
        using HttpResponseMessage response = await SendApiAsync(HttpMethod.Get, endpoint, accessToken, clientId: null, ct);
        return await ReadAsync<MalListPage>(response, ct);
    }

    public async Task<MalAnime> GetAnimeAsync(int malId, string? accessToken, CancellationToken ct = default)
    {
        string endpoint = $"{ApiBase}/anime/{malId}?fields={Uri.EscapeDataString(ListFields)}";
        using HttpResponseMessage response = await SendApiAsync(
            HttpMethod.Get, endpoint, accessToken, _config.CurrentValue.ClientId, ct);
        return await ReadAsync<MalAnime>(response, ct);
    }

    public async Task<List<MalAnime>> SearchAnimeAsync(string query, string? accessToken, CancellationToken ct = default)
    {
        string endpoint = $"{ApiBase}/anime?q={Uri.EscapeDataString(query)}&limit=20&nsfw=true&fields=" +
                          Uri.EscapeDataString(ListFields);
        using HttpResponseMessage response = await SendApiAsync(
            HttpMethod.Get, endpoint, accessToken, _config.CurrentValue.ClientId, ct);
        MalSearchPage page = await ReadAsync<MalSearchPage>(response, ct);
        return page.Data.Select(x => x.Node).ToList();
    }

    public async Task<MalListStatus> UpdateListStatusAsync(
        string accessToken,
        int malId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken ct = default)
    {
        using FormUrlEncodedContent content = new(values);
        using HttpResponseMessage response = await SendApiAsync(
            HttpMethod.Put, $"{ApiBase}/anime/{malId}/my_list_status", accessToken, clientId: null, ct, content);
        return await ReadAsync<MalListStatus>(response, ct);
    }

    public async Task DeleteListEntryAsync(string accessToken, int malId, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await SendApiAsync(
            HttpMethod.Delete, $"{ApiBase}/anime/{malId}/my_list_status", accessToken, clientId: null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            throw new MalApiException(response.StatusCode, $"MyAnimeList delete failed: {body}");
        }
    }

    async Task<HttpResponseMessage> SendApiAsync(
        HttpMethod method,
        string url,
        string? accessToken,
        string? clientId,
        CancellationToken ct,
        HttpContent? content = null)
    {
        byte[]? body = content is null ? null : await content.ReadAsByteArrayAsync(ct);
        string? contentType = content?.Headers.ContentType?.ToString();

        for (int attempt = 0; ; attempt++)
        {
            await WaitForSlotAsync(ct);
            using HttpRequestMessage request = new(method, url);
            if (body is not null)
            {
                ByteArrayContent requestContent = new(body);
                if (!string.IsNullOrEmpty(contentType))
                    requestContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
                request.Content = requestContent;
            }
            if (!string.IsNullOrWhiteSpace(accessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            else if (!string.IsNullOrWhiteSpace(clientId))
                request.Headers.Add("X-MAL-CLIENT-ID", clientId);

            HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                return response;

            int delaySeconds = response.Headers.RetryAfter?.Delta?.TotalSeconds is double seconds
                ? (int)seconds
                : attempt switch { 0 => 5, 1 => 15, _ => 30 };
            response.Dispose();
            if (attempt >= 2)
                throw new MalApiException(HttpStatusCode.TooManyRequests, "MyAnimeList rate limit; try again later.");

            _logger.LogWarning("MyAnimeList rate limited; retrying in {Delay}s", delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
        }
    }

    static async Task WaitForSlotAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            TimeSpan wait = _nextRequestAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _nextRequestAt = DateTime.UtcNow.AddMilliseconds(650);
        }
        finally
        {
            Gate.Release();
        }
    }

    static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new MalApiException(response.StatusCode, $"MyAnimeList request failed: {body}");
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
            ?? throw new MalApiException(response.StatusCode, "MyAnimeList returned an empty response.");
    }
}

public sealed class MalApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
