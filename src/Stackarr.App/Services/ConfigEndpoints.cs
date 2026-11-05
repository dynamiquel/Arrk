using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Stackarr.Auth.Abstractions;
using Stackarr.Core.Configuration;

namespace Stackarr.App.Services;

/// <summary>
/// The only HTTP surface that exposes configuration shape and validation. The live config itself
/// (including provider keys, VPN credentials and tunnel tokens) is read only by the admin-only
/// <c>/config</c> page and written only by admin-only endpoints; both APIs below require Admin.
/// </summary>
public static class ConfigEndpoints
{
    public static void MapStackarrConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/config/schema", (ConfigSchemaGenerator schemaGenerator) =>
        {
            return Results.Ok(schemaGenerator.GenerateSchema());
        }).RequireAuthorization(StackarrPolicies.AdminOnly);

        endpoints.MapPost("/api/config/validate", (ConfigValidationRequest request, ConfigValidator configValidator) =>
        {
            ConfigValidationResult result = configValidator.ValidateWithFluentValidation(request.Content);
            return Results.Ok(result);
        }).RequireAuthorization(StackarrPolicies.AdminOnly);
    }
}

public record ConfigValidationRequest(string Content);
