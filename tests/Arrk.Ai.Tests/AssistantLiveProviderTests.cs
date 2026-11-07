using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arrk.Ai.Tests;

/// <summary>
/// Optional live diagnostics for the provider path. Only runs when <c>ARRK_LIVE_AI_KEY</c> is
/// set. These exist because OpenAI-compatible endpoints differ in how they treat tool schemas, and a
/// failure there must be visible instead of being reported as a generic provider error.
/// </summary>
public class AssistantLiveProviderTests
{
    static AiConfig? LiveConfig()
    {
        string? key = Environment.GetEnvironmentVariable("ARRK_LIVE_AI_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return new AiConfig
        {
            Enabled = true,
            Provider = Environment.GetEnvironmentVariable("ARRK_LIVE_AI_PROVIDER") ?? AiProviders.Gemini,
            ApiKey = key,
            Models = new AiModelsConfig
            {
                Medium = Environment.GetEnvironmentVariable("ARRK_LIVE_AI_MODEL") ?? "gemini-3.6-flash"
            },
            TimeoutSeconds = 60,
            Features = new AiFeaturesConfig
            {
                Assistant = new AiAssistantFeature { MaxMessagesPerHour = 0, MaxTokensPerUserPerDay = 0 }
            }
        };
    }

    [Test]
    public async Task PlainStreamingTurn_IsAccepted()
    {
        AiConfig? config = LiveConfig();
        if (config is null)
            return;

        IChatClient client = new AiClientFactory(NullLogger<AiClientFactory>.Instance)
            .Create(config, AiModelRole.Medium)!;

        List<string> chunks = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "Reply with exactly: READY")]))
        {
            if (!string.IsNullOrEmpty(update.Text))
                chunks.Add(update.Text);
        }

        await Assert.That(chunks.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task StreamingTurnWithATool_IsAccepted()
    {
        AiConfig? config = LiveConfig();
        if (config is null)
            return;

        IChatClient client = new AiClientFactory(NullLogger<AiClientFactory>.Instance)
            .Create(config, AiModelRole.Medium)!;
        AIFunction tool = AIFunctionFactory.Create(
            (string query) => $"found:{query}", "echo_lookup", "Looks up a test value.");

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "Call echo_lookup with query=dune, then answer.")],
                           new ChatOptions { Tools = [tool], ToolMode = ChatToolMode.Auto }))
        {
            updates.Add(update);
        }

        await Assert.That(updates.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task ToolRoundThroughTheAssistant_IsAccepted()
    {
        AiConfig? config = LiveConfig();
        if (config is null)
            return;

        using AssistantTestHost host = new(config, packs: [new EchoAssistantTool()]);
        AssistantService service = host.CreateService();

        List<AssistantUpdate> updates = [];
        await foreach (AssistantUpdate update in service.StreamAsync(
                           AssistantTestHost.User(),
                           "Use the echo_lookup tool with query=dune, then answer with the value it returned."))
        {
            updates.Add(update);
        }

        await Assert.That(updates.OfType<AssistantFailed>().Count()).IsEqualTo(0);
        string answer = string.Concat(updates.OfType<AssistantTextDelta>().Select(u => u.Text));
        await Assert.That(answer.Length).IsGreaterThan(0);

        foreach (AssistantToolFinished finished in updates.OfType<AssistantToolFinished>())
            await Assert.That(finished.Success).IsTrue();
    }
}
