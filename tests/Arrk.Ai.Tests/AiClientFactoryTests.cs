using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Ai;
using Arrk.TestSupport;

namespace Arrk.Ai.Tests;

/// <summary>
/// The AI foundation is opt-in and advisory, so the factory must always degrade to <c>null</c>
/// (the app keeps running) for missing/unknown/blank configuration, and must produce a working
/// client, hitting the right endpoint, for a valid one.
/// </summary>
public class AiClientFactoryTests
{
    static AiClientFactory Factory(FakeHttpMessageHandler? transport = null) =>
        new(NullLogger<AiClientFactory>.Instance, transport);

    static string ChatCompletion(string content) =>
        $$"""
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "test-model",
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": {{System.Text.Json.JsonSerializer.Serialize(content)}} }, "finish_reason": "stop" }
          ]
        }
        """;

    [Test]
    public async Task Create_ReturnsNull_WhenDisabled()
    {
        IChatClient? client = Factory().Create(new AiConfig { Enabled = false, Models = new AiModelsConfig { Medium = "m" }, Provider = AiProviders.Gemini, ApiKey = "k" });

        await Assert.That(client).IsNull();
    }

    [Test]
    public async Task Create_ReturnsNull_WhenModelMissing()
    {
        IChatClient? client = Factory().Create(new AiConfig { Enabled = true, Provider = AiProviders.Gemini, Models = new AiModelsConfig { Medium = "" }, ApiKey = "k" });

        await Assert.That(client).IsNull();
    }

    [Test]
    public async Task Create_ReturnsNull_ForUnknownProvider()
    {
        IChatClient? client = Factory().Create(new AiConfig { Enabled = true, Provider = "not-a-provider", Models = new AiModelsConfig { Medium = "m" }, ApiKey = "k" });

        await Assert.That(client).IsNull();
    }

    [Test]
    public async Task Create_ReturnsNull_WhenHostedProviderHasNoKey()
    {
        IChatClient? openai = Factory().Create(new AiConfig { Enabled = true, Provider = AiProviders.OpenAi, Models = new AiModelsConfig { Medium = "gpt-4o-mini" }, ApiKey = "" });
        IChatClient? anthropic = Factory().Create(new AiConfig { Enabled = true, Provider = AiProviders.Anthropic, Models = new AiModelsConfig { Medium = "claude-sonnet-4-6" } });

        await Assert.That(openai).IsNull();
        await Assert.That(anthropic).IsNull();
    }

    [Test]
    public async Task Create_ReturnsClient_ForLocalProviderWithoutKey()
    {
        IChatClient? ollama = Factory().Create(new AiConfig { Enabled = true, Provider = AiProviders.Ollama, Models = new AiModelsConfig { Medium = "llama3.1:8b" }, ApiKey = "" });
        IChatClient? lmStudio = Factory().Create(new AiConfig { Enabled = true, Provider = AiProviders.LmStudio, Models = new AiModelsConfig { Medium = "local-model" } });

        await Assert.That(ollama).IsNotNull();
        await Assert.That(lmStudio).IsNotNull();
    }

    [Test]
    public async Task TryResolveEndpoint_UsesProviderDefaults()
    {
        AiClientFactory factory = Factory();

        await Assert.That(factory.TryResolveEndpoint(new AiConfig { Provider = AiProviders.Gemini }, out Uri? gemini)).IsTrue();
        await Assert.That(gemini).IsEqualTo(AiProviders.GeminiEndpoint);

        await Assert.That(factory.TryResolveEndpoint(new AiConfig { Provider = AiProviders.Ollama }, out Uri? ollama)).IsTrue();
        await Assert.That(ollama).IsEqualTo(AiProviders.OllamaEndpoint);

        // Claude goes through Anthropic's OpenAI-compatibility layer, so it is just another URL.
        await Assert.That(factory.TryResolveEndpoint(new AiConfig { Provider = AiProviders.Anthropic }, out Uri? anthropic)).IsTrue();
        await Assert.That(anthropic).IsEqualTo(AiProviders.AnthropicEndpoint);

        await Assert.That(factory.TryResolveEndpoint(new AiConfig { Provider = AiProviders.OpenAi }, out Uri? openai)).IsTrue();
        await Assert.That(openai).IsEqualTo(AiProviders.OpenAiEndpoint);
    }

    [Test]
    public async Task EveryPresetExceptCustom_HasADefaultEndpoint()
    {
        AiClientFactory factory = Factory();

        foreach (string provider in AiProviders.All.Where(p => p != AiProviders.Custom))
        {
            await Assert.That(factory.TryResolveEndpoint(new AiConfig { Provider = provider }, out Uri? endpoint)).IsTrue();
            await Assert.That(endpoint).IsNotNull();
        }
    }

    [Test]
    public async Task TryResolveEndpoint_BaseUrlOverridesThePreset()
    {
        AiClientFactory factory = Factory();
        AiConfig config = new() { Provider = AiProviders.OpenAi, BaseUrl = "https://gateway.example/v1" };

        await Assert.That(factory.TryResolveEndpoint(config, out Uri? endpoint)).IsTrue();
        await Assert.That(endpoint).IsEqualTo(new Uri("https://gateway.example/v1"));
    }

    [Test]
    public async Task TryResolveEndpoint_RequiresBaseUrl_ForCustomProvider()
    {
        await Assert.That(Factory().TryResolveEndpoint(new AiConfig { Provider = AiProviders.Custom }, out _)).IsFalse();
    }

    [Test]
    public async Task TryResolveEndpoint_HonoursBaseUrl_AndRejectsInvalid()
    {
        AiClientFactory factory = Factory();

        AiConfig withBaseUrl = new() { Provider = AiProviders.Custom, BaseUrl = "https://gateway.example/v1" };
        await Assert.That(factory.TryResolveEndpoint(withBaseUrl, out Uri? resolved)).IsTrue();
        await Assert.That(resolved).IsEqualTo(new Uri("https://gateway.example/v1"));

        await Assert.That(factory.TryResolveEndpoint(new AiConfig { Provider = AiProviders.OpenAi, BaseUrl = "not a url" }, out _)).IsFalse();
        await Assert.That(factory.TryResolveEndpoint(new AiConfig { Provider = AiProviders.OpenAi, BaseUrl = "ftp://nope" }, out _)).IsFalse();
    }

    [Test]
    public async Task Create_ClientTargetsGeminiEndpoint_AndReturnsTheCompletion()
    {
        FakeHttpMessageHandler transport = new();
        transport.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(ChatCompletion("hello")));

        AiConfig config = new()
        {
            Enabled = true,
            Provider = AiProviders.Gemini,
            Models = new AiModelsConfig { Medium = "gemini-2.5-flash" },
            ApiKey = "secret-key"
        };

        IChatClient? client = Factory(transport).Create(config);
        await Assert.That(client).IsNotNull();

        ChatResponse response = await client!.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        await Assert.That(response.Text).IsEqualTo("hello");
        await Assert.That(transport.Requests).Count().IsEqualTo(1);
        await Assert.That(transport.Requests[0].Uri.AbsoluteUri)
            .IsEqualTo("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions");
        await Assert.That(transport.Requests[0].AuthParameter).IsEqualTo("secret-key");
    }
}
