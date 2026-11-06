using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Arrk.Auth.Abstractions;
using Arrk.Core.Playback;

namespace Arrk.Jellyfin.Playback;

/// <summary>
/// HTTP surface for the in-site web player:
/// <list type="bullet">
/// <item><c>POST /api/media/player/{itemId}/start</c>, negotiate playback and return a plan.</item>
/// <item><c>GET /api/media/player/{itemId}/next</c>, the following episode for autoplay (204 when none).</item>
/// <item><c>GET /api/media/player/image/{itemId}</c>, authenticated still image for the autoplay overlay.</item>
/// <item><c>GET|HEAD /api/media/player/stream/{sessionId}/{token}/{**path}</c>, the stream proxy.
/// It is path-preserving (Jellyfin's HLS playlists use relative URIs) and injects the Jellyfin
/// token server-side, so the browser never holds it. It authenticates with the signed
/// <c>token</c> path segment rather than a cookie so AirPlay receivers can fetch it too.</item>
/// <item><c>POST /api/media/player/{sessionId}/stop</c>, end a session and free the transcode.</item>
/// </list>
/// Everything is additive and guarded by the feature flag, so removing the player is just removing
/// the call to <see cref="MapJellyfinPlaybackEndpoints"/> and this folder.
/// </summary>
public static class PlaybackEndpoints
{
    public static void MapJellyfinPlaybackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/media/player")
            .RequireAuthorization(ArrkPolicies.Authenticated);

        group.MapPost("/{itemId}/start", StartAsync);
        group.MapGet("/{itemId}/next", NextAsync);
        group.MapGet("/image/{itemId}", ImageAsync);
        group.MapPost("/{sessionId}/stop", StopAsync);
        group.MapPost("/{sessionId}/complete", CompleteAsync);
        group.MapPost("/{sessionId}/progress", ProgressAsync);

        // The stream proxy is NOT behind the auth policy. An AirPlay receiver fetches
        // the playlist and segments itself, with none of the browser's cookies, so it authenticates
        // with the signed token embedded in the path instead (see StreamTokenService).
        endpoints.MapMethods(
            "/api/media/player/stream/{sessionId}/{token}/{**path}",
            ["GET", "HEAD"],
            ProxyAsync);
    }

    static async Task<IResult> StartAsync(
        string itemId,
        PlaybackStartRequest request,
        HttpContext http,
        JellyfinPlaybackService playback,
        JellyfinClient client,
        JellyfinWatchActivityRecorder activity,
        CancellationToken ct)
    {
        if (!playback.IsEnabled)
            return Results.NotFound();

        string? username = http.User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return Results.Unauthorized();

        PlaybackPlan? plan = await playback.StartAsync(
            username,
            itemId,
            request.Capabilities ?? new BrowserCapabilities(),
            request.StartTicks,
            request.AudioStreamIndex,
            request.SubtitleStreamIndex,
            ct);

        if (plan is null)
            return Results.NotFound();

        JellyfinItem? item = await client.GetItemAsync(itemId, ct);
        if (item is not null)
        {
            (string? device, string? clientName) = PlaybackDevice.FromUserAgent(http.Request.Headers.UserAgent);
            await activity.RecordAsync(
                username,
                item,
                WatchActivitySources.Arrk,
                device,
                clientName,
                http.Connection.RemoteIpAddress?.ToString(),
                $"arrk-player:{plan.SessionId}",
                plan.StartTicks,
                completed: false,
                ct: ct);
        }

        return Results.Ok(plan);
    }

    static async Task<IResult> NextAsync(
        string itemId,
        JellyfinPlaybackService playback,
        CancellationToken ct)
    {
        PlaybackNextEpisode? next = await playback.GetNextEpisodeAsync(itemId, ct);
        return next is null ? Results.NoContent() : Results.Ok(next);
    }

    /// <summary>
    /// Serves an item's still image for the autoplay overlay. Jellyfin needs the admin token, which
    /// must never reach the browser, so the image travels through Arrk's authenticated origin.
    /// </summary>
    static async Task<IResult> ImageAsync(
        string itemId,
        JellyfinClient client,
        CancellationToken ct)
    {
        if (!Guid.TryParse(itemId, out _))
            return Results.BadRequest();

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendProxyAsync(
                HttpMethod.Get,
                $"Items/{Uri.EscapeDataString(itemId)}/Images/Primary?maxHeight=360&maxWidth=640&quality=80",
                rangeHeader: null,
                ct);
        }
        catch
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        using (upstream)
        {
            if (!upstream.IsSuccessStatusCode)
                return Results.StatusCode((int)upstream.StatusCode);

            byte[] image = await upstream.Content.ReadAsByteArrayAsync(ct);
            string contentType = upstream.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return Results.File(image, contentType);
        }
    }

    static async Task<IResult> StopAsync(
        string sessionId,
        HttpContext http,
        PlaybackSessionStore sessions,
        JellyfinClient client,
        CancellationToken ct)
    {
        string? username = http.User.Identity?.Name;
        PlaybackSession? session = username is null ? null : sessions.GetOwned(sessionId, username);
        if (session is null)
            return Results.Ok(new { stopped = false });

        sessions.Remove(sessionId);

        // Best effort: free the Jellyfin transcode job now rather than waiting for it to idle out.
        try
        {
            await client.DeleteActiveEncodingAsync(session.PlaySessionId, ct);
        }
        catch
        {
            /* the transcode will be reaped by Jellyfin's own idle timer */
        }

        return Results.Ok(new { stopped = true });
    }

    static async Task<IResult> CompleteAsync(
        string sessionId,
        HttpContext http,
        PlaybackSessionStore sessions,
        JellyfinPlaybackService playback,
        JellyfinClient client,
        JellyfinWatchActivityRecorder activity,
        CancellationToken ct)
    {
        string? username = http.User.Identity?.Name;
        PlaybackSession? session = username is null ? null : sessions.GetOwned(sessionId, username);
        if (session is null)
            return Results.Ok(new { completed = false });

        await playback.MarkCompletedAsync(session.Username, session.ItemId, ct);

        JellyfinItem? item = await client.GetItemAsync(session.ItemId, ct);
        if (item is not null)
        {
            await activity.RecordAsync(
                session.Username,
                item,
                WatchActivitySources.Arrk,
                device: null,
                clientName: null,
                ipAddress: null,
                sessionKey: $"arrk-player:{sessionId}",
                positionTicks: item.RunTimeTicks ?? 0,
                completed: true,
                ct: ct);
        }

        return Results.Ok(new { completed = true });
    }

    static async Task<IResult> ProgressAsync(
        string sessionId,
        PlaybackProgressUpdate update,
        HttpContext http,
        PlaybackSessionStore sessions,
        JellyfinPlaybackService playback,
        JellyfinClient client,
        JellyfinWatchActivityRecorder activity,
        CancellationToken ct)
    {
        string? username = http.User.Identity?.Name;
        PlaybackSession? session = username is null ? null : sessions.GetOwned(sessionId, username);
        if (session is null)
            return Results.Ok(new { saved = false });

        await playback.ReportProgressAsync(session.Username, session.ItemId, update.PositionTicks, ct);

        JellyfinItem? item = await client.GetItemAsync(session.ItemId, ct);
        if (item is not null)
        {
            await activity.RecordAsync(
                session.Username,
                item,
                WatchActivitySources.Arrk,
                device: null,
                clientName: null,
                ipAddress: null,
                sessionKey: $"arrk-player:{sessionId}",
                positionTicks: update.PositionTicks,
                completed: false,
                ct: ct);
        }

        return Results.Ok(new { saved = true });
    }

    static async Task ProxyAsync(
        string sessionId,
        string token,
        string? path,
        HttpContext http,
        StreamTokenService streamTokens,
        PlaybackSessionStore sessions,
        JellyfinClient client,
        CancellationToken ct)
    {
        // Token-first authentication: the token is bound to this session id and expires, so a
        // cookie-less AirPlay receiver can fetch the stream without gaining access to any other
        // session. The signed-in browser keeps working through its cookie if the token is stale.
        // sessions.Get also refreshes LastSeenAt so a long title is not reaped mid-stream.
        bool tokenValid = streamTokens.TryValidate(
            token, JellyfinPlaybackService.MediaStreamScope, out string subject, out _)
            && string.Equals(subject, sessionId, StringComparison.Ordinal);

        PlaybackSession? session;
        if (tokenValid)
        {
            session = sessions.Get(sessionId);
        }
        else
        {
            string? username = http.User.Identity?.Name;
            if (username is null)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            session = sessions.GetOwned(sessionId, username);
        }

        if (session is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Only proxied, relative Jellyfin paths are allowed, never an absolute URL or traversal,
        // which would otherwise turn this endpoint into an SSRF primitive.
        if (string.IsNullOrEmpty(path)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Rebuild the Jellyfin path exactly as the browser requested it (path + query) so relative
        // HLS URIs keep resolving through this proxy.
        string jellyfinPath = (path ?? "") + http.Request.QueryString;

        HttpMethod method = HttpMethods.IsHead(http.Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        string? range = http.Request.Headers.Range.FirstOrDefault();

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendProxyAsync(method, jellyfinPath, range, ct);
        }
        catch (Exception)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (upstream)
        {
            http.Response.StatusCode = (int)upstream.StatusCode;
            // Never let a CDN/proxy (Cloudflare) cache playback bytes or playlists.
            http.Response.Headers["Cache-Control"] = "no-store";
            string? contentType = upstream.Content.Headers.ContentType?.ToString();

            if (HlsPlaylistRewriter.IsPlaylist(contentType, path))
            {
                string playlist = await upstream.Content.ReadAsStringAsync(ct);
                playlist = HlsPlaylistRewriter.StripAuthToken(playlist);
                http.Response.ContentType = "application/vnd.apple.mpegurl";
                if (method != HttpMethod.Head)
                    await http.Response.WriteAsync(playlist, ct);
                return;
            }

            if (WebVttRewriter.IsWebVtt(contentType, path))
            {
                string vtt = await upstream.Content.ReadAsStringAsync(ct);
                vtt = WebVttRewriter.RaiseCues(vtt);
                http.Response.ContentType = "text/vtt; charset=utf-8";
                if (method != HttpMethod.Head)
                    await http.Response.WriteAsync(vtt, ct);
                return;
            }

            if (!string.IsNullOrEmpty(contentType))
                http.Response.ContentType = contentType;

            CopyHeader(upstream, http, "Accept-Ranges");
            CopyHeader(upstream, http, "Content-Range");
            CopyHeader(upstream, http, "Content-Length");

            if (method == HttpMethod.Head)
                return;

            await upstream.Content.CopyToAsync(http.Response.Body, ct);
        }
    }

    static void CopyHeader(HttpResponseMessage upstream, HttpContext http, string name)
    {
        if (upstream.Content.Headers.TryGetValues(name, out IEnumerable<string>? values))
            http.Response.Headers[name] = values.ToArray();
        else if (upstream.Headers.TryGetValues(name, out IEnumerable<string>? generalValues))
            http.Response.Headers[name] = generalValues.ToArray();
    }
}
