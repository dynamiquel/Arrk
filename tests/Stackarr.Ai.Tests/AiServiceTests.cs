using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Ai;
using Stackarr.TestSupport;

namespace Stackarr.Ai.Tests;

/// <summary>
/// <see cref="AiService"/> is the single seam feature code will use. It must report disabled state
/// honestly (so callers keep their deterministic path) and otherwise forward a prompt with the
/// configured system prompt prepended.
/// </summary>
public class AiServiceTests
{
    static AiService Build(AiConfig config, FakeHttpMessageHandler? transport = null) =>
        new(new AiClientFactory(NullLogger<AiClientFactory>.Instance, transport),
            new MutableOptionsMonitor<AiConfig>(config),
            NullLogger<AiService>.Instance);

    [Test]
    public async Task Disabled_ReportsDisabled_AndAskReturnsNull()
    {
        AiService service = Build(new AiConfig { Enabled = false });

        await Assert.That(service.Enabled).IsFalse();
        await Assert.That(service.ChatClient).IsNull();
        await Assert.That(await service.AskAsync("anything")).IsNull();
    }

    [Test]
    public async Task Enabled_ExposesClient_AndSendsSystemPromptThenUser()
    {
        FakeHttpMessageHandler transport = new();
        transport.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""
            { "id":"x","object":"chat.completion","created":1,"model":"m",
              "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}] }
            """));

        AiConfig config = new()
        {
            Enabled = true,
            Provider = AiProviders.Ollama,
            Models = new AiModelsConfig { Chat = "llama3.1:8b" },
            SystemPrompt = "SYSTEM-MARKER"
        };

        AiService service = Build(config, transport);

        await Assert.That(service.Enabled).IsTrue();
        await Assert.That(service.ChatClient).IsNotNull();

        string? answer = await service.AskAsync("USER-MARKER");

        await Assert.That(answer).IsEqualTo("ok");
        await Assert.That(transport.Requests).Count().IsEqualTo(1);
        await Assert.That(transport.Requests[0].Body).Contains("SYSTEM-MARKER");
        await Assert.That(transport.Requests[0].Body).Contains("USER-MARKER");
    }

    [Test]
    public async Task ConfigReload_EnablesAiWithoutRestart()
    {
        MutableOptionsMonitor<AiConfig> monitor = new(new AiConfig { Enabled = false });
        AiService service = new(
            new AiClientFactory(NullLogger<AiClientFactory>.Instance),
            monitor,
            NullLogger<AiService>.Instance);

        await Assert.That(service.Enabled).IsFalse();

        monitor.Update(new AiConfig
        {
            Enabled = true,
            Provider = AiProviders.Ollama,
            Models = new AiModelsConfig { Chat = "llama3.1:8b" }
        });

        await Assert.That(service.Enabled).IsTrue();
        await Assert.That(service.Config.ResolveChatModel()).IsEqualTo("llama3.1:8b");
    }

    [Test]
    public async Task Usage_IsRecordedForACall()
    {
        using TestAiDb db = new();
        AiUsageService usage = new(db, TimeProvider.System);
        FakeHttpMessageHandler transport = new();
        transport.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""
            { "id":"x","object":"chat.completion","created":1,"model":"m",
              "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
              "usage":{"prompt_tokens":42,"completion_tokens":7,"total_tokens":49} }
            """));

        AiConfig config = new()
        {
            Enabled = true,
            Provider = AiProviders.Ollama,
            Models = new AiModelsConfig { Chat = "m" }
        };

        AiService service = new(
            new AiClientFactory(NullLogger<AiClientFactory>.Instance, transport),
            new MutableOptionsMonitor<AiConfig>(config),
            NullLogger<AiService>.Instance,
            usage);

        await service.AskAsync("hello", AiModelRole.Chat, "assistant", "chat");

        AiUsageSummary summary = await usage.GetSummaryAsync(DateTime.UtcNow.AddHours(-1));
        await Assert.That(summary.Calls).IsEqualTo(1);
        await Assert.That(summary.PromptTokens).IsEqualTo(42);
        await Assert.That(summary.CompletionTokens).IsEqualTo(7);
    }

    [Test]
    public async Task Ask_BlankPrompt_DoesNotCallTheProvider()
    {
        FakeHttpMessageHandler transport = new();
        AiService service = Build(new AiConfig { Enabled = true, Provider = AiProviders.Ollama, Models = new AiModelsConfig { Chat = "m" } }, transport);

        await Assert.That(await service.AskAsync("   ")).IsNull();
        await Assert.That(transport.Requests).IsEmpty();
    }
}
