using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Stackarr.Auth.Abstractions;
using Stackarr.Core.Enums;
using Stackarr.Core.Playback;
using Stackarr.Navidrome.Playback;

namespace Stackarr.Navidrome;

/// <summary>
/// HTTP surface for the in-site music player. Mirrors the Jellyfin playback proxy: it streams
/// bytes from Navidrome server-side (holding the Subsonic credentials) and passes Range headers
/// through for seeking, so the browser never sees Navidrome's token and can play/seek directly.
/// </summary>
public static class NavidromePlaybackEndpoints
{
    /// <summary>Token scope for music stream URLs (see <see cref="StreamTokenService"/>).</summary>
    public const string MusicStreamScope = "music";

    /// <summary>Lifetime of a music stream token. Long enough for any track; refreshed per page load.</summary>
    public static readonly TimeSpan StreamTokenLifetime = TimeSpan.FromHours(6);

    /// <summary>Query parameter carrying the signed stream token.</summary>
    public const string StreamTokenQuery = "st";

    public static void MapNavidromePlaybackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/music/player")
            .RequireAuthorization(StackarrPolicies.Authenticated);

        group.MapGet("/token", IssueTokenAsync);
        group.MapGet("/lyrics/{songId}", LyricsAsync);
        group.MapMethods("/cover/{coverArtId}", ["GET", "HEAD"], CoverAsync);
        group.MapPost("/scrobble/{songId}", ScrobbleAsync);
        group.MapPost("/queue", SaveQueueAsync);

        // The stream is NOT behind the auth policy: AirPlay hands the URL to the
        // receiver, which fetches it with none of the browser's cookies. It authenticates with the
        // signed token in the query string instead (see StreamTokenService).
        endpoints.MapMethods("/api/music/player/stream/{songId}", ["GET", "HEAD"], StreamAsync);
    }

    /// <summary>
    /// A song's lyrics for the in-site player, in Stackarr's own shape (see
    /// <see cref="MusicLyricsMapper"/>). Streamed through Stackarr for the same reason the audio is:
    /// the browser never holds Navidrome's Subsonic credentials. 204 means the song has no lyrics yet.
    /// </summary>
    static async Task LyricsAsync(
        string songId,
        HttpContext http,
        NavidromeService navidrome,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(songId))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        List<SubsonicStructuredLyrics> tracks = await navidrome.GetLyricsAsync(songId, ct);
        LyricsResponse? payload = MusicLyricsMapper.ToResponse(songId, tracks);
        if (payload is null)
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await http.Response.WriteAsJsonAsync(payload, ct);
    }

    /// <summary>
    /// Mints a short-lived stream token for the signed-in user. The mini-player fetches this once
    /// and appends it to each track URL so an AirPlay receiver can fetch the audio without cookies.
    /// </summary>
    static IResult IssueTokenAsync(HttpContext http, StreamTokenService streamTokens)
    {
        string? username = http.User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return Results.Unauthorized();

        string token = streamTokens.Create(MusicStreamScope, username, StreamTokenLifetime);
        return Results.Ok(new
        {
            token,
            expiresAt = DateTimeOffset.UtcNow.Add(StreamTokenLifetime)
        });
    }

    static async Task SaveQueueAsync(
        HttpContext http,
        NavidromeClient client,
        CancellationToken ct)
    {
        try
        {
            QueueSave? body = await http.Request.ReadFromJsonAsync<QueueSave>(ct);
            if (body?.Ids is { Count: > 0 })
                await client.SavePlayQueueAsync(body.Ids, body.Current, body.Position, ct);
        }
        catch
        {
            /* best effort */
        }
    }

    public sealed class QueueSave
    {
        public List<string> Ids { get; set; } = [];
        public string Current { get; set; } = "";
        public long Position { get; set; }
    }

    static async Task StreamAsync(
        string songId,
        HttpContext http,
        StreamTokenService streamTokens,
        NavidromeClient client,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(songId))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Token-first authentication: a cookie-less AirPlay receiver can fetch the track with a
        // signed-in user's short-lived token. The browser keeps working through its cookie if the
        // token is missing or stale. (The subject is the username; it is not needed to resolve the
        // song, so it is only checked for presence.)
        string? token = http.Request.Query[StreamTokenQuery].FirstOrDefault();
        bool tokenValid = streamTokens.TryValidate(token, MusicStreamScope, out string subject, out _)
            && !string.IsNullOrEmpty(subject);

        if (!tokenValid && string.IsNullOrEmpty(http.User.Identity?.Name))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        string? range = http.Request.Headers.Range.FirstOrDefault();

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendStreamAsync(songId, range, ct);
        }
        catch (Exception)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (upstream)
        {
            await CopyAsync(http, upstream, HttpMethods.IsHead(http.Request.Method), ct);
        }
    }

    static async Task CoverAsync(
        string coverArtId,
        HttpContext http,
        NavidromeClient client,
        CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage upstream = await client.SendCoverArtAsync(coverArtId, ct);
            http.Response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentType is not null)
                http.Response.ContentType = upstream.Content.Headers.ContentType.ToString();
            http.Response.Headers["Cache-Control"] = "public, max-age=86400";
            if (!HttpMethods.IsHead(http.Request.Method))
                await upstream.Content.CopyToAsync(http.Response.Body, ct);
        }
        catch (Exception)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    static async Task ScrobbleAsync(
        string songId,
        HttpContext http,
        NavidromeClient client,
        MusicLibraryService library,
        WatchActivityService watch,
        CancellationToken ct)
    {
        try
        {
            bool submission = !http.Request.Query.TryGetValue("submission", out var value)
                || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            await client.ScrobbleAsync(songId, submission, ct);

            ScrobbleBody body = new();
            try { body = await http.Request.ReadFromJsonAsync<ScrobbleBody>(ct) ?? new(); }
            catch { /* metadata is optional */ }

            string? username = http.User.Identity?.Name;
            int.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userId);

            if (!submission && int.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int playUserId))
            {
                // Keep the small per-user "Recently Played" history in step with the richer activity log.
                await library.RecordPlayAsync(playUserId, songId, body.Title, body.Artist, body.Album, body.CoverArtUrl, ct);
            }

            if (!string.IsNullOrEmpty(username))
            {
                string itemKey = MediaItemKeys.MusicTrack(body.Artist, body.Title);
                if (!submission)
                {
                    (string? device, string? clientName) = PlaybackDevice.FromUserAgent(http.Request.Headers.UserAgent);
                    await watch.RecordAsync(new WatchActivityRecord
                    {
                        Username = username,
                        UserId = userId > 0 ? userId : null,
                        ProviderKey = WatchActivityServices.Music,
                        Domain = new MediaDomain("music"),
                        ItemType = "track",
                        ItemKey = itemKey,
                        ParentItemKey = MediaItemKeys.MusicAlbum(string.IsNullOrWhiteSpace(body.AlbumArtist) ? body.Artist : body.AlbumArtist, body.Album),
                        Title = string.IsNullOrWhiteSpace(body.Title) ? "Unknown track" : body.Title,
                        Subtitle = string.IsNullOrWhiteSpace(body.Artist) ? null : body.Artist,
                        ImageUrl = string.IsNullOrWhiteSpace(body.CoverArtUrl) ? null : body.CoverArtUrl,
                        Source = WatchActivitySources.Stackarr,
                        Device = device,
                        Client = clientName,
                        IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                        StartedAt = DateTime.UtcNow,
                        LastSeenAt = DateTime.UtcNow
                    }, ct);
                }
                else if (!string.IsNullOrEmpty(itemKey))
                {
                    await watch.UpdateProgressAsync(username, WatchActivityServices.Music, itemKey, 0, 0, completed: false, ct: ct);
                }
            }

            await http.Response.WriteAsJsonAsync(new { scrobbled = true }, ct);
        }
        catch (Exception)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    public sealed class ScrobbleBody
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string AlbumArtist { get; set; } = "";
        public string Album { get; set; } = "";
        public string CoverArtUrl { get; set; } = "";
    }

    static async Task CopyAsync(HttpContext http, HttpResponseMessage upstream, bool head, CancellationToken ct)
    {
        http.Response.StatusCode = (int)upstream.StatusCode;
        http.Response.Headers["Cache-Control"] = "no-store";

        if (upstream.Content.Headers.ContentType is not null)
            http.Response.ContentType = upstream.Content.Headers.ContentType.ToString();

        CopyHeader(upstream, http, "Accept-Ranges");
        CopyHeader(upstream, http, "Content-Range");
        CopyHeader(upstream, http, "Content-Length");

        if (head)
            return;

        await upstream.Content.CopyToAsync(http.Response.Body, ct);
    }

    static void CopyHeader(HttpResponseMessage upstream, HttpContext http, string name)
    {
        if (upstream.Content.Headers.TryGetValues(name, out IEnumerable<string>? values))
            http.Response.Headers[name] = values.ToArray();
        else if (upstream.Headers.TryGetValues(name, out IEnumerable<string>? general))
            http.Response.Headers[name] = general.ToArray();
    }
}
