using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Ai;
using Arrk.Orchestration.Tools;

namespace Arrk.Orchestration.Tests;

/// <summary>
/// The operations pack is admin-only and must fail softly when a concrete orchestration service is
/// not registered (the pack itself is resolved from a plain module container in some compositions).
/// </summary>
public class OrchestrationAssistantToolTests
{
    static AssistantToolContext Context()
    {
        ServiceCollection services = new();
        return new AssistantToolContext(
            1, "alice", true, services.BuildServiceProvider(), CancellationToken.None);
    }

    static AIFunction Tool(string name) =>
        (AIFunction)new OrchestrationAssistantTool().CreateTools(Context()).Single(t => t.Name == name);

    [Test]
    public async Task PackIsAdminOnly_AndExposesOpsTools()
    {
        OrchestrationAssistantTool pack = new();

        await Assert.That(pack.Access).IsEqualTo(AssistantAccess.Admin);
        await Assert.That(pack.CreateTools(Context()).Select(t => t.Name).ToArray()).IsEquivalentTo(
            new[]
            {
                "ops_host_status", "ops_storage", "ops_jobs", "ops_image_updates", "ops_recent_events"
            });
    }

    [Test]
    public async Task HostStatus_ExplainsWhenTheServiceIsMissing()
    {
        JsonElement json = (JsonElement)(await Tool("ops_host_status").InvokeAsync(
            new AIFunctionArguments()))!;

        await Assert.That(json.GetProperty("error").GetString()).Contains("not available");
    }

    [Test]
    public async Task Storage_ExplainsWhenConfigurationIsMissing()
    {
        JsonElement json = (JsonElement)(await Tool("ops_storage").InvokeAsync(
            new AIFunctionArguments()))!;

        await Assert.That(json.GetProperty("error").GetString()).Contains("not available");
    }
}
