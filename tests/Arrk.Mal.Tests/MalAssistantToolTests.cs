using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Ai;
using Arrk.Mal.Tools;
using Arrk.TestSupport;

namespace Arrk.Mal.Tests;

/// <summary>
/// MAL title matching and pack guardrails. The matcher must prefer an exact English/primary title,
/// ignore punctuation, and keep split cours ambiguous rather than picking the first one.
/// </summary>
public class MalAssistantToolTests
{
    static MalConfig Config(bool configured = true) => new()
    {
        Enabled = configured,
        ClientId = configured ? "client" : "",
        ClientSecret = configured ? "secret" : ""
    };

    static MalAssistantTool Pack(bool configured = true) =>
        new(new MutableOptionsMonitor<MalConfig>(Config(configured)));

    static AssistantToolContext Context()
    {
        ServiceCollection services = new();
        return new AssistantToolContext(
            1, "alice", false, services.BuildServiceProvider(), CancellationToken.None);
    }

    static AIFunction Tool(string name) =>
        (AIFunction)Pack().CreateTools(Context()).Single(t => t.Name == name);

    [Test]
    public async Task TitleMatch_PrefersExactAndIgnoresPunctuation()
    {
        await Assert.That(MalTitleMatcher.Score("re zero", "Re:Zero kara Hajimeru Isekai Seikatsu"))
            .IsEqualTo(70);
        await Assert.That(MalTitleMatcher.Score("frieren", "Frieren: Beyond Journey's End"))
            .IsEqualTo(70);
        await Assert.That(MalTitleMatcher.Score("beyond journey", "Frieren: Beyond Journey's End"))
            .IsEqualTo(50);
        await Assert.That(MalTitleMatcher.Score("naruto", "Sousou no Frieren")).IsEqualTo(0);
    }

    [Test]
    public async Task TitleMatch_PrefersAnExactTitleOverASynonym()
    {
        int score = MalTitleMatcher.Score("attack on titan", "Shingeki no Kyojin", "Attack on Titan");
        await Assert.That(score).IsEqualTo(100);
    }

    [Test]
    public async Task PackAvailabilityFollowsConfig()
    {
        await Assert.That(Pack().IsAvailable).IsTrue();
        await Assert.That(Pack(configured: false).IsAvailable).IsFalse();
    }

    [Test]
    public async Task Update_ExplainsWhenTheIntegrationIsMissing()
    {
        JsonElement json = (JsonElement)(await Tool("mal_update").InvokeAsync(
            new AIFunctionArguments { ["title"] = "Frieren", ["status"] = "watching" }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(json.GetProperty("message").GetString()).Contains("not available");
    }

    [Test]
    public async Task Update_RejectsAnInvalidScoreBeforeTouchingTheService()
    {
        JsonElement json = (JsonElement)(await Tool("mal_update").InvokeAsync(
            new AIFunctionArguments { ["title"] = "Frieren", ["score"] = 11 }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(json.GetProperty("message").GetString()).Contains("between 0 and 10");
    }
}
