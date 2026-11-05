using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Auth.Abstractions;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// Raw-body upload endpoint for local media. The browser streams the file straight to disk, so a
/// multi-gigabyte remux does not pass through the Blazor circuit or a multipart temp copy. The
/// custom header is a CSRF guard: a cross-origin request that tries to set it must pass a CORS
/// preflight, which this server never grants.
/// </summary>
public static class TvUploadEndpoints
{
    public const string UploadHeader = "X-Stackarr-Upload";

    public static void MapTvUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Local uploads write straight into the shared library, so they are administrator-only
        // just like deleting or unmonitoring media. Non-admins use the request flow instead.
        RouteGroupBuilder group = endpoints.MapGroup("/api/tv/uploads")
            .RequireAuthorization(StackarrPolicies.AdminOnly);

        group.MapPost("/", UploadAsync).DisableAntiforgery();
        group.MapGet("/batch/{batchId}", GetBatchAsync);
        group.MapDelete("/batch/{batchId}", CancelBatchAsync);
    }

    static async Task<IResult> UploadAsync(
        HttpContext http,
        [FromQuery] string batchId,
        [FromQuery] string kind,
        [FromQuery] string mediaType,
        [FromQuery] string fileName,
        TvUploadStore store,
        CancellationToken ct)
    {
        if (!http.Request.Headers.ContainsKey(UploadHeader))
            return Results.BadRequest(new { error = "Missing upload header." });

        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest(new { error = "A file name is required." });

        if (!TvUploadFormats.IsSupported(fileName))
        {
            return Results.BadRequest(new
            {
                error = $"\"{fileName}\" is not supported. Upload one of: {TvUploadFormats.SupportedLabel}."
            });
        }

        long maxBytes = store.MaxFileSizeBytes;
        IHttpMaxRequestBodySizeFeature? sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = maxBytes + (1024 * 1024);

        try
        {
            TvUploadRecord record = await store.StageAsync(
                http.Request.Body,
                fileName,
                http.Request.ContentLength,
                batchId,
                kind,
                mediaType,
                ct);

            return Results.Ok(ToResponse(record));
        }
        catch (TvUploadException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
    }

    static async Task<IResult> GetBatchAsync(string batchId, TvUploadStore store, CancellationToken ct)
    {
        if (!TvUploadStore.IsValidBatchId(batchId))
            return Results.BadRequest(new { error = "Unknown upload batch." });

        List<TvUploadRecord> records = await store.GetBatchAsync(batchId, ct);
        return Results.Ok(records.Select(ToResponse).ToList());
    }

    static async Task<IResult> CancelBatchAsync(string batchId, TvUploadStore store, CancellationToken ct)
    {
        if (!TvUploadStore.IsValidBatchId(batchId))
            return Results.BadRequest(new { error = "Unknown upload batch." });

        await store.CancelBatchAsync(batchId, ct);
        return Results.NoContent();
    }

    static object ToResponse(TvUploadRecord record) => new
    {
        id = record.Id,
        batchId = record.BatchId,
        fileName = record.FileName,
        sizeBytes = record.SizeBytes,
        status = record.Status,
        error = record.Error,
        kind = record.Kind,
        mediaType = record.MediaType
    };
}
