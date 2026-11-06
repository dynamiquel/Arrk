using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Ai;
using Arrk.Requests.Tools;
using Arrk.TestSupport;

namespace Arrk.Requests.Tests;

/// <summary>
/// The requests pack must gate moderation before it touches the scoped request service. The service
/// itself trusts its caller, so a non-admin tool call must stop at the pack.
/// </summary>
public class RequestsAssistantToolTests
{
    static RequestsAssistantTool Pack() => new(new MutableOptionsMonitor<RequestsConfig>(new RequestsConfig()));

    static AssistantToolContext Context(bool admin = false)
    {
        ServiceCollection services = new();
        return new AssistantToolContext(
            1, "alice", admin, services.BuildServiceProvider(), CancellationToken.None);
    }

    static AIFunction Tool(AssistantToolContext context, string name) =>
        (AIFunction)Pack().CreateTools(context).Single(t => t.Name == name);

    [Test]
    public async Task Approve_IsAdminOnly()
    {
        JsonElement json = (JsonElement)(await Tool(Context(), "requests_approve").InvokeAsync(
            new AIFunctionArguments { ["id"] = 7L }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(json.GetProperty("message").GetString()).Contains("administrator");
    }

    [Test]
    public async Task ApproveAll_IsAdminOnly()
    {
        JsonElement json = (JsonElement)(await Tool(Context(), "requests_approve_all").InvokeAsync(
            new AIFunctionArguments { ["limit"] = 10 }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(json.GetProperty("message").GetString()).Contains("administrator");
    }

    [Test]
    public async Task List_ExplainsWhenTheServiceIsMissing()
    {
        JsonElement json = (JsonElement)(await Tool(Context(), "requests_list").InvokeAsync(
            new AIFunctionArguments { ["status"] = "pending" }))!;

        await Assert.That(json.GetProperty("error").GetString()).Contains("turned off");
    }

    [Test]
    public async Task PackIsAvailableToUsers()
    {
        await Assert.That(Pack().Access).IsEqualTo(AssistantAccess.User);
        await Assert.That(Pack().CreateTools(Context()).Select(t => t.Name).ToArray()).IsEquivalentTo(
            new[]
            {
                "requests_list", "requests_summary", "requests_approve", "requests_deny",
                "requests_approve_all", "requests_revoke"
            });
    }
}
