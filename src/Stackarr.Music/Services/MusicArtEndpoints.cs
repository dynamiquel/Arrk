using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Stackarr.Auth.Abstractions;

namespace Stackarr.Music.Services;

/// <summary>
/// Cached artwork resolution for music items that don't carry a direct image URL (library
/// entries added without a Deezer cover). Resolves via Deezer server-side and redirects the
/// browser to the CDN, so artwork loads lazily in parallel and never blocks a page render.
/// </summary>
public static class MusicArtEndpoints
{
    public static void MapMusicArtEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/music/art")
            .RequireAuthorization(StackarrPolicies.Authenticated);

        group.MapGet("/artist", ArtistAsync);
        group.MapGet("/album", AlbumAsync);
    }

    static async Task<IResult> ArtistAsync(string? name, DeezerService deezer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Results.NotFound();
        string url = await deezer.GetArtistImageAsync(name, ct);
        return string.IsNullOrEmpty(url) ? Results.NotFound() : Results.Redirect(url);
    }

    static async Task<IResult> AlbumAsync(string? artist, string? title, DeezerService deezer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Results.NotFound();
        string url = await deezer.GetAlbumCoverAsync(artist ?? "", title, ct);
        return string.IsNullOrEmpty(url) ? Results.NotFound() : Results.Redirect(url);
    }
}
