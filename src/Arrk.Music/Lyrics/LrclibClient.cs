using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Arrk.Music.Lyrics;

/// <summary>
/// Keyless client for LRCLIB, the community synced-lyrics database Arrk uses as its lyrics
/// provider. Returns the provider's own records; choosing and verifying one is the matcher's job.
///
/// LRCLIB is a small, donation-funded service: it rate-limits and returns transient
/// <c>429</c>/<c>5xx</c> failures under load, which surface as
/// <see cref="LyricsProviderUnavailableException"/> so callers can back off rather than record a
/// permanent miss.
/// </summary>
public class LrclibClient
{
    const string BaseUrl = "https://lrclib.net/";

    /// <summary>LRCLIB asks clients to identify themselves.</summary>
    const string UserAgent = "Arrk (+https://github.com/arrk)";

    /// <summary>Attempts per request, including the first. LRCLIB's busy responses usually clear
    /// within a second or two.</summary>
    const int MaxAttempts = 3;

    /// <summary>Upper bound on a <c>Retry-After</c> hint we will actually sleep for.</summary>
    static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http;
    readonly ILogger<LrclibClient>? _logger;

    public LrclibClient(ILogger<LrclibClient>? logger = null)
        : this(logger, null)
    {
    }

    public LrclibClient(ILogger<LrclibClient>? logger, HttpMessageHandler? handler)
    {
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>
    /// Exact lookup by signature: artist, track, optional album and duration. LRCLIB matches all
    /// supplied fields, so dropping the album widens the search, <c>null</c> when nothing matches.
    /// </summary>
    public async Task<LyricsCandidate?> GetAsync(
        string artist, string track, string? album, int? durationSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(track))
            return null;

        List<string> query =
        [
            $"artist_name={Uri.EscapeDataString(artist)}",
            $"track_name={Uri.EscapeDataString(track)}"
        ];
        if (!string.IsNullOrWhiteSpace(album))
            query.Add($"album_name={Uri.EscapeDataString(album)}");
        if (durationSeconds is > 0)
            query.Add($"duration={durationSeconds}");

        (HttpStatusCode status, string body) = await SendAsync($"api/get?{string.Join('&', query)}", ct);
        if (status == HttpStatusCode.NotFound)
            return null;
        ThrowIfUnavailable(status, $"get {track}");
        return Deserialize<LyricsCandidate>(body);
    }

    /// <summary>
    /// Ranked candidate search. Supply <paramref name="trackName"/>/<paramref name="artistName"/> for
    /// structured matching, or <paramref name="query"/> for free text; LRCLIB returns an array
    /// (possibly empty).
    /// </summary>
    public async Task<List<LyricsCandidate>> SearchAsync(
        string? trackName = null,
        string? artistName = null,
        string? query = null,
        CancellationToken ct = default)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(trackName))
            parts.Add($"track_name={Uri.EscapeDataString(trackName)}");
        if (!string.IsNullOrWhiteSpace(artistName))
            parts.Add($"artist_name={Uri.EscapeDataString(artistName)}");
        if (!string.IsNullOrWhiteSpace(query))
            parts.Add($"q={Uri.EscapeDataString(query)}");
        if (parts.Count == 0)
            return [];

        (HttpStatusCode status, string body) = await SendAsync($"api/search?{string.Join('&', parts)}", ct);
        if (status == HttpStatusCode.NotFound)
            return [];
        ThrowIfUnavailable(status, "search");
        return Deserialize<List<LyricsCandidate>>(body) ?? [];
    }

    /// <summary>
    /// Sends a request, retrying the transient failures LRCLIB produces under load. Its limiter answers
    /// <c>429</c>/<c>503</c> ("The server is busy, please retry in a moment") even for queries that
    /// succeed a second later, so a single attempt would mark whole batches of songs as unreachable.
    /// </summary>
    async Task<(HttpStatusCode Status, string Body)> SendAsync(string relativeUrl, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using HttpResponseMessage response = await _http.GetAsync(relativeUrl, ct);
            HttpStatusCode status = response.StatusCode;
            string body = await response.Content.ReadAsStringAsync(ct);

            if (attempt >= MaxAttempts || !IsTransient(status))
                return (status, body);

            TimeSpan delay = RetryDelay(response, attempt);
            _logger?.LogDebug(
                "LRCLIB returned {Status} for {Url}; retrying in {Delay}ms (attempt {Attempt}/{Max})",
                (int)status, relativeUrl, (int)delay.TotalMilliseconds, attempt, MaxAttempts);
            await Task.Delay(delay, ct);
        }
    }

    static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            || (int)status >= 500;

    static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        // Honour Retry-After when it asks for something sane; otherwise back off exponentially.
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta <= MaxRetryAfter)
            return delta;
        return TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt - 1));
    }

    void ThrowIfUnavailable(HttpStatusCode status, string operation)
    {
        if (status is HttpStatusCode.OK or HttpStatusCode.NoContent)
            return;

        // Any other status is a provider problem, not a verdict on the song: surface it so the caller
        // can retry later rather than record a permanent miss.
        _logger?.LogDebug("LRCLIB {Operation} returned {Status}", operation, (int)status);
        throw new LyricsProviderUnavailableException($"LRCLIB {operation} failed with HTTP {(int)status}.");
    }

    T? Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            // An unexpected body (e.g. an error object where an array was expected) is not something
            // we can read as "no lyrics", surface it so the lookup is retried, not recorded as final.
            _logger?.LogDebug(ex, "Unexpected LRCLIB payload");
            throw new LyricsProviderUnavailableException("LRCLIB returned an unexpected payload.", ex);
        }
    }
}
