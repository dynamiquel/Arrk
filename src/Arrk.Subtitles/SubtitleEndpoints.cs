using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Arrk.Auth.Abstractions;
using Arrk.Subtitles.Abstractions;

namespace Arrk.Subtitles;

/// <summary>
/// Additive HTTP surface for subtitle status, manual search and requests. The Blazor UI calls the
/// same <see cref="SubtitleService"/>, so these endpoints exist for integrations and future clients
/// rather than being the only way in.
/// </summary>
public static class SubtitleEndpoints
{
    public static void MapSubtitleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/subtitles")
            .RequireAuthorization(ArrkPolicies.Authenticated);

        group.MapGet("/languages", GetLanguagesAsync);
        group.MapGet("/status", GetStatusAsync);
        group.MapPost("/search", SearchAsync);
        group.MapPost("/download", DownloadAsync);
        group.MapPost("/request", RequestAsync);
    }

    static async Task<IResult> GetLanguagesAsync(SubtitleService subtitles, CancellationToken ct)
    {
        IReadOnlyList<SubtitleLanguage> languages = await subtitles.GetLanguagesAsync(ct);
        return Results.Ok(languages);
    }

    static async Task<IResult> GetStatusAsync(
        string kind,
        int id,
        int? seriesId,
        int? season,
        SubtitleService subtitles,
        CancellationToken ct)
    {
        if (!TryTarget(kind, id, seriesId, season, out SubtitleTarget? target))
            return Results.BadRequest("Unknown subtitle target.");

        SubtitleStatus? status = await subtitles.GetStatusAsync(target, ct);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    static async Task<IResult> SearchAsync(
        SubtitleSearchApiRequest request,
        SubtitleService subtitles,
        CancellationToken ct)
    {
        if (!TryTarget(request.Kind, request.Id, request.SeriesId, request.SeasonNumber, out SubtitleTarget? target))
            return Results.BadRequest("Unknown subtitle target.");

        target = target with { Title = request.Title, Subtitle = request.Subtitle };
        IReadOnlyList<SubtitleCandidate> candidates =
            await subtitles.SearchAsync(target, request.Languages, ct);
        return Results.Ok(candidates);
    }

    static async Task<IResult> DownloadAsync(
        SubtitleDownloadApiRequest request,
        SubtitleService subtitles,
        CancellationToken ct)
    {
        if (!subtitles.IsAvailable)
            return Results.Problem("No subtitle provider is enabled.", statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!TryTarget(request.Kind, request.Id, request.SeriesId, request.SeasonNumber, out SubtitleTarget? target))
            return Results.BadRequest("Unknown subtitle target.");

        target = target with { Title = request.Title, Subtitle = request.Subtitle };
        await subtitles.DownloadAsync(target, request.Candidate, ct);
        return Results.NoContent();
    }

    static async Task<IResult> RequestAsync(
        SubtitleRequestApiRequest request,
        SubtitleService subtitles,
        CancellationToken ct)
    {
        if (!subtitles.IsAvailable)
            return Results.Problem("No subtitle provider is enabled.", statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!TryTarget(request.Kind, request.Id, request.SeriesId, request.SeasonNumber, out SubtitleTarget? target))
            return Results.BadRequest("Unknown subtitle target.");

        target = target with { Title = request.Title, Subtitle = request.Subtitle };
        await subtitles.RequestAsync(target, request.Languages, ct);
        return Results.NoContent();
    }

    static bool TryTarget(
        string kind,
        int id,
        int? seriesId,
        int? season,
        out SubtitleTarget target)
    {
        target = SubtitleTarget.Movie(0);
        if (!Enum.TryParse(kind, ignoreCase: true, out SubtitleMediaKind parsed))
            return false;

        target = parsed switch
        {
            SubtitleMediaKind.Movie when id > 0 => SubtitleTarget.Movie(id),
            SubtitleMediaKind.Episode when seriesId is > 0 && id > 0 => SubtitleTarget.Episode(seriesId.Value, id, season ?? 0),
            SubtitleMediaKind.Season when seriesId is > 0 || id > 0 => SubtitleTarget.Season(seriesId ?? id, season ?? 0),
            SubtitleMediaKind.Series when id > 0 => SubtitleTarget.Series(id),
            _ => SubtitleTarget.Movie(0)
        };

        return parsed switch
        {
            SubtitleMediaKind.Movie => target.MovieId > 0,
            SubtitleMediaKind.Episode => target.SeriesId > 0 && target.EpisodeId > 0,
            SubtitleMediaKind.Season => target.SeriesId > 0,
            SubtitleMediaKind.Series => target.SeriesId > 0,
            _ => false
        };
    }
}

public sealed class SubtitleSearchApiRequest
{
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    public int? SeriesId { get; set; }
    public int? SeasonNumber { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<string> Languages { get; set; } = [];
}

public sealed class SubtitleDownloadApiRequest
{
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    public int? SeriesId { get; set; }
    public int? SeasonNumber { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public SubtitleCandidate Candidate { get; set; } = new() { Provider = "", Id = "" };
}

public sealed class SubtitleRequestApiRequest
{
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    public int? SeriesId { get; set; }
    public int? SeasonNumber { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<string> Languages { get; set; } = [];
}
