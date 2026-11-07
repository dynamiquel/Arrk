using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Arrk.Auth.Abstractions;
using Arrk.Core.Documentation;
using Arrk.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Ai.Tests;

public class McpServerTests
{
    [Test]
    public async Task Mcp_UsesLiveAccountPermissions_AndDoesNotRequireAnAiProvider()
    {
        AiConfig config = new()
        {
            Mcp = new McpConfig
            {
                Enabled = true,
                Clients = [new() { UserId = 7, Token = new string('x', 40) }]
            }
        };
        SessionReader users = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IOptionsMonitor<AiConfig>>(new MutableOptionsMonitor<AiConfig>(config));
        builder.Services.AddSingleton<IUserSessionReader>(users);
        DocumentationCatalog documents = new([new PluginDocumentation()]);
        builder.Services.AddSingleton(documents);
        builder.Services.AddSingleton(new AssistantRegistry(
            [new ToolsPack(), new ToolsPack(admin: true), new Tools.DocumentationAssistantTool(documents)], []));
        builder.Services.AddArrkMcp();
        await using WebApplication app = builder.Build();
        app.MapArrkMcp();
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using HttpClient client = new() { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using (HttpResponseMessage response = await Send(client, "tools/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using (HttpResponseMessage response = await Send(client, "resources/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using (HttpResponseMessage response = await Send(client, "resources/read", new { uri = "arrk://documentation/plugin/start/plugin-task-34" }))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Add("Cookie", "arrk.auth=not-a-bearer-credential");
        using (HttpResponseMessage response = await Send(client, "tools/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Remove("Cookie");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('z', 40));
        using (HttpResponseMessage response = await Send(client, "tools/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('x', 40));
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        using (HttpResponseMessage response = await Send(client, "tools/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove("Origin");
        using (HttpResponseMessage response = await Send(client, "initialize", new
        {
            protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" }
        }))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string readOnly = await Body(client, "tools/list");
        await Assert.That(readOnly).Contains("search_media");
        await Assert.That(readOnly).Contains("search_documentation");
        await Assert.That(readOnly).Contains("read_documentation");
        await Assert.That(readOnly).DoesNotContain("requests_approve");
        await Assert.That(readOnly).DoesNotContain("ops_host_status");

        // Plugin content comes from the local provider, without a model or a public website.
        using JsonDocument resources = JsonDocument.Parse(await Body(client, "resources/list"));
        JsonElement firstPage = resources.RootElement.GetProperty("result");
        await Assert.That(firstPage.GetProperty("resources").GetArrayLength()).IsEqualTo(32);
        string cursor = firstPage.GetProperty("nextCursor").GetString()!;
        using JsonDocument remainder = JsonDocument.Parse(await Body(client, "resources/list", new { cursor }));
        await Assert.That(remainder.RootElement.GetProperty("result").GetProperty("resources").GetArrayLength()).IsEqualTo(4);
        string resource = await Body(client, "resources/read", new { uri = "arrk://documentation/plugin/start/plugin-task-34" });
        await Assert.That(resource).Contains("Plugin instructions 34");
        await Assert.That(resource).DoesNotContain("Plugin instructions 33");
        await Assert.That(await Body(client, "resources/read", new { uri = "file:///etc/passwd" })).Contains("Unknown documentation resource");
        await Assert.That(await Body(client, "resources/list", new { cursor = "-1" })).Contains("Invalid documentation cursor");

        string denied = await Body(client, "tools/call", new { name = "requests_approve", arguments = new { } });
        await Assert.That(denied).Contains("not permitted");

        string scoped = await Body(client, "tools/call", new { name = "search_media", arguments = new { } });
        await Assert.That(scoped).Contains("alice:7");

        config.Mcp.Clients[0].AllowWrites = true;
        string writes = await Body(client, "tools/list");
        await Assert.That(writes).Contains("requests_approve");
        await Assert.That(writes).DoesNotContain("tv_delete_media");
        await Assert.That(writes).DoesNotContain("ops_host_status");
        await Assert.That(await Body(client, "tools/call", new { name = "ops_host_status", arguments = new { } }))
            .Contains("not permitted");

        users.Admin = true;
        string admin = await Body(client, "tools/list");
        await Assert.That(admin).Contains("ops_host_status");
        foreach (string destructive in new[] { "tv_delete_media", "music_delete_media", "tv_upgrade_quality" })
            await Assert.That(await Body(client, "tools/call", new { name = destructive, arguments = new { } }))
                .Contains("not permitted");

        users.Admin = false;
        await Assert.That(await Body(client, "tools/call", new { name = "ops_host_status", arguments = new { } }))
            .Contains("not permitted");
        users.Pending = true;
        using (HttpResponseMessage response = await Send(client, "tools/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        users.Pending = false;
        users.Deleted = true;
        using (HttpResponseMessage response = await Send(client, "tools/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        users.Deleted = false;

        users.Active = false;
        using (HttpResponseMessage response = await Send(client, "tools/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using (HttpResponseMessage response = await Send(client, "resources/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using (HttpResponseMessage response = await Send(client, "resources/read", new { uri = "arrk://documentation/plugin/start/overview" }))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        users.Active = true;
        config.Mcp.Clients[0].Token = new string('y', 40);
        using (HttpResponseMessage response = await Send(client, "tools/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        config.Mcp.Enabled = false;
        using (HttpResponseMessage response = await Send(client, "tools/list"))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    static Task<HttpResponseMessage> Send(HttpClient client, string method, object? parameters = null) =>
        client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } });

    static async Task<string> Body(HttpClient client, string method, object? parameters = null)
    {
        using HttpResponseMessage response = await Send(client, method, parameters);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        return response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..].TrimEnd('\r')
            : body;
    }

    sealed class PluginDocumentation : IDocumentationProvider
    {
        public IReadOnlyList<DocumentationDocument> Documents { get; } =
        [new("plugin/start", "# Plugin help\n\nPlugin overview.\n\n" + string.Join("\n\n",
            Enumerable.Range(0, 35).Select(i => $"## Plugin task {i}\n\nPlugin instructions {i}")))];
    }

    sealed class SessionReader : IUserSessionReader
    {
        public bool Active { get; set; } = true;
        public bool Admin { get; set; }
        public bool Pending { get; set; }
        public bool Deleted { get; set; }
        public Task<string> GetSecurityStampAsync(ArrkUser user) => Task.FromResult("stamp");
        public Task<ArrkSessionState?> GetSessionStateAsync(int id) => Task.FromResult<ArrkSessionState?>(Deleted ? null : new(
            new ArrkUser { Id = id, Username = "alice" },
            [Pending ? ArrkRoles.Pending : Admin ? ArrkRoles.Admin : ArrkRoles.User],
            Pending ? UserRole.Pending : Admin ? UserRole.Admin : UserRole.User, Active, "stamp"));
    }

    sealed class ToolsPack(bool admin = false) : IAssistantTool
    {
        public string Key => admin ? "ops" : "library";
        public string Name => Key;
        public string Description => "Test";
        public AssistantAccess Access => admin ? AssistantAccess.Admin : AssistantAccess.User;
        public IReadOnlyList<string> Keywords => [];
        public bool IsAvailable => true;
        public IReadOnlyList<AITool> CreateTools(AssistantToolContext context) => admin
            ? [AIFunctionFactory.Create(() => "host", "ops_host_status")]
            : [AIFunctionFactory.Create(() => $"{context.Username}:{context.UserId}", "search_media"),
                AIFunctionFactory.Create(() => "write", "requests_approve"),
                AIFunctionFactory.Create(() => "delete", "tv_delete_media")];
    }
}
