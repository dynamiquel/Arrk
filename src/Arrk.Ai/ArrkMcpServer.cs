using System.Security.Cryptography;
using System.Text;
using Arrk.Auth.Abstractions;
using Arrk.Core.Documentation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Arrk.Ai;

/// <summary>Stateless, credential-scoped MCP adapter over the same module tools as the assistant.</summary>
public static class ArrkMcpServer
{
    const string CallerKey = "Arrk.Mcp.Caller";

    // Fail closed: new tools are not automatically read-only just because their name sounds safe.
    static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "search_media", "get_media", "get_continue_watching", "get_capabilities", "get_help",
        "search_documentation", "read_documentation", "tv_recommend_media", "music_recommend_media",
        "downloads_progress", "downloads_failures", "ops_host_status", "ops_storage", "ops_jobs",
        "ops_image_updates", "ops_recent_events", "requests_list", "requests_summary", "mal_list"
    };

    public static IServiceCollection AddArrkMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithListResourcesHandler((request, ct) =>
            {
                DocumentationCatalog documents = request.Services!.GetRequiredService<DocumentationCatalog>();
                List<Resource> resources = documents.Topics.SelectMany(topic => topic.Sections.Select(section => new Resource
                {
                    Uri = ResourceUri(topic.Id, section.Id),
                    Name = topic.Title + " — " + section.Title,
                    MimeType = "text/markdown"
                })).ToList();
                string? cursor = request.Params?.Cursor;
                int offset = 0;
                if (cursor is not null && (!int.TryParse(cursor, out offset) || offset < 0 || offset > resources.Count))
                    throw new McpProtocolException("Invalid documentation cursor.", McpErrorCode.InvalidParams);
                return ValueTask.FromResult(new ListResourcesResult
                {
                    Resources = resources.Skip(offset).Take(32).ToList(),
                    NextCursor = offset + 32 < resources.Count ? (offset + 32).ToString(System.Globalization.CultureInfo.InvariantCulture) : null
                });
            })
            .WithReadResourceHandler((request, ct) =>
            {
                DocumentationCatalog documents = request.Services!.GetRequiredService<DocumentationCatalog>();
                string? uri = request.Params?.Uri;
                foreach (DocumentationTopic topic in documents.Topics)
                foreach (DocumentationSection section in topic.Sections)
                {
                    // Exact registered identifiers only: never resolve caller-supplied paths or URLs.
                    if (ResourceUri(topic.Id, section.Id) != uri)
                        continue;
                    return ValueTask.FromResult(new ReadResourceResult
                    {
                        Contents = [new TextResourceContents
                        {
                            Uri = uri, MimeType = "text/markdown", Text = documents.ReadSection(topic.Id, section.Id)!
                        }]
                    });
                }
                throw new McpProtocolException("Unknown documentation resource.", McpErrorCode.ResourceNotFound);
            })
            .WithListToolsHandler((request, ct) =>
            {
                HttpContext http = Http(request.Services!);
                ListToolsResult result = new()
                {
                    Tools = Tools(http, ct).Select(f => McpServerTool.Create(f).ProtocolTool).ToList()
                };
                return ValueTask.FromResult(result);
            })
            .WithCallToolHandler(async (request, ct) =>
            {
                HttpContext http = Http(request.Services!);
                AIFunction? function = Tools(http, ct).FirstOrDefault(f => f.Name == request.Params?.Name);
                if (function is null)
                    return Error("Tool unavailable or not permitted.");
                try
                {
                    return await McpServerTool.Create(function).InvokeAsync(request, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch
                {
                    return Error("The tool could not complete. Check Arrk's service status and arguments.");
                }
            });
        return services;
    }

    static string ResourceUri(string topic, string section) => $"arrk://documentation/{topic}/{section}";

    static HttpContext Http(IServiceProvider services) =>
        services.GetRequiredService<IHttpContextAccessor>().HttpContext
        ?? throw new InvalidOperationException("MCP requires an HTTP request.");

    static IReadOnlyList<AIFunction> Tools(HttpContext http, CancellationToken ct)
    {
        McpCaller caller = (McpCaller)http.Items[CallerKey]!;
        AssistantToolContext context = new(caller.User.Id, caller.User.Username, caller.User.IsAdmin,
            http.RequestServices, ct);
        return http.RequestServices.GetRequiredService<AssistantRegistry>().AllTools(context)
            // An external model cannot supply the trusted Arrk confirmation-button click.
            .Where(f => f.Name is not ("tv_delete_media" or "music_delete_media" or "tv_upgrade_quality"))
            .Where(f => caller.AllowWrites || ReadTools.Contains(f.Name)).ToList();
    }

    static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    public static void MapArrkMcp(this IEndpointRouteBuilder endpoints)
    {
        // This branch deliberately does not accept browser cookies. Bearer-only auth avoids CSRF
        // and DNS-rebinding access through an already signed-in browser.
        endpoints.MapGroup("/mcp").AddEndpointFilter(async (invocation, next) =>
        {
            HttpContext http = invocation.HttpContext;
            McpConfig config = http.RequestServices.GetRequiredService<IOptionsMonitor<AiConfig>>().CurrentValue.Mcp;
            if (!config.Enabled)
                return Results.NotFound();

            string origin = http.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                || !string.Equals(uri.GetLeftPart(UriPartial.Authority),
                    $"{http.Request.Scheme}://{http.Request.Host}", StringComparison.OrdinalIgnoreCase)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            string authorization = http.Request.Headers.Authorization.ToString();
            string token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization[7..] : "";
            McpClientConfig? client = config.Clients.FirstOrDefault(c => Matches(c.Token, token));
            if (client is null)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Unauthorized();
            }

            IUserSessionReader users = http.RequestServices.GetRequiredService<IUserSessionReader>();
            ArrkSessionState? state = await users.GetSessionStateAsync(client.UserId);
            if (state is not { IsActive: true } || state.Role == UserRole.Pending
                || !state.Roles.Any(r => r is ArrkRoles.User or ArrkRoles.Admin))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            http.Items[CallerKey] = new McpCaller(
                new AssistantUser(state.User.Id, state.User.Username, state.Roles.Contains(ArrkRoles.Admin)),
                client.AllowWrites);
            return await next(invocation);
        }).MapMcp().AllowAnonymous();
    }

    static bool Matches(string expected, string actual) => expected.Length >= 32 && actual.Length >= 32
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual)));

    sealed record McpCaller(AssistantUser User, bool AllowWrites);
}
