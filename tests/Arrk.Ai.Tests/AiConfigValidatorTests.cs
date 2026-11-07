using FluentValidation.Results;
using Arrk.Ai;

namespace Arrk.Ai.Tests;

/// <summary>
/// A disabled/absent AI section must stay valid (the app can't be blocked by an optional feature);
/// an enabled one must carry a usable provider, model and credentials.
/// </summary>
public class AiConfigValidatorTests
{
    static ValidationResult Validate(AiConfig config) => new AiConfigValidator().Validate(config);

    [Test]
    public async Task Disabled_EmptySection_IsValid()
    {
        await Assert.That(Validate(new AiConfig()).IsValid).IsTrue();
    }

    [Test]
    public async Task Mcp_RequiresUniqueStrongCredentials_EvenWithoutAnAiProvider()
    {
        AiConfig config = new() { Mcp = new McpConfig { Enabled = true } };
        await Assert.That(Validate(config).IsValid).IsFalse();
        config.Mcp.Clients.Add(new McpClientConfig { UserId = 7, Token = new string('x', 32) });
        await Assert.That(Validate(config).IsValid).IsTrue();
        config.Mcp.Clients.Add(new McpClientConfig { UserId = 8, Token = new string('x', 32) });
        await Assert.That(Validate(config).IsValid).IsFalse();
        config.Mcp.Clients[1].Token = "short";
        await Assert.That(Validate(config).IsValid).IsFalse();
        config.Mcp.Clients[1].Token = new string('y', 32);
        config.Mcp.Clients[1].UserId = 0;
        await Assert.That(Validate(config).IsValid).IsFalse();
    }

    [Test]
    public async Task Enabled_GeminiWithKeyAndModel_IsValid()
    {
        AiConfig config = new()
        {
            Enabled = true,
            Provider = AiProviders.Gemini,
            Models = new AiModelsConfig { Chat = "gemini-2.5-flash" },
            ApiKey = "key"
        };

        await Assert.That(Validate(config).IsValid).IsTrue();
    }

    [Test]
    public async Task Enabled_LocalOrCustomProvider_NeedsNoKey()
    {
        await Assert.That(Validate(new AiConfig { Enabled = true, Provider = AiProviders.Ollama, Models = new AiModelsConfig { Chat = "llama3.1:8b" } }).IsValid).IsTrue();
        await Assert.That(Validate(new AiConfig { Enabled = true, Provider = AiProviders.LmStudio, Models = new AiModelsConfig { Chat = "local" } }).IsValid).IsTrue();
        await Assert.That(Validate(new AiConfig
        {
            Enabled = true,
            Provider = AiProviders.Custom,
            Models = new AiModelsConfig { Chat = "m" },
            BaseUrl = "http://localhost:1234/v1"
        }).IsValid).IsTrue();
    }

    [Test]
    public async Task Enabled_AnthropicWithKey_IsValid()
    {
        AiConfig config = new()
        {
            Enabled = true,
            Provider = AiProviders.Anthropic,
            Models = new AiModelsConfig { Chat = "claude-sonnet-4-6" },
            ApiKey = "key"
        };

        await Assert.That(Validate(config).IsValid).IsTrue();
    }

    [Test]
    public async Task Enabled_UnknownProvider_IsInvalid()
    {
        AiConfig config = new() { Enabled = true, Provider = "bogus", Models = new AiModelsConfig { Chat = "m" }, ApiKey = "k" };

        await Assert.That(Validate(config).IsValid).IsFalse();
    }

    [Test]
    public async Task Enabled_HostedProviderWithoutKey_IsInvalid()
    {
        AiConfig config = new() { Enabled = true, Provider = AiProviders.OpenAi, Models = new AiModelsConfig { Chat = "gpt-4o-mini" } };

        await Assert.That(Validate(config).IsValid).IsFalse();
    }

    [Test]
    public async Task Enabled_WithoutModel_IsInvalid()
    {
        AiConfig config = new() { Enabled = true, Provider = AiProviders.Gemini, Models = new AiModelsConfig { Chat = "" }, ApiKey = "k" };

        await Assert.That(Validate(config).IsValid).IsFalse();
    }

    [Test]
    public async Task Enabled_CustomProvider_RequiresBaseUrl()
    {
        AiConfig withoutUrl = new() { Enabled = true, Provider = AiProviders.Custom, Models = new AiModelsConfig { Chat = "m" } };
        await Assert.That(Validate(withoutUrl).IsValid).IsFalse();

        AiConfig withUrl = new() { Enabled = true, Provider = AiProviders.Custom, Models = new AiModelsConfig { Chat = "m" }, BaseUrl = "https://gateway.example/v1" };
        await Assert.That(Validate(withUrl).IsValid).IsTrue();
    }

    [Test]
    public async Task Enabled_WithModelsChat_IsValid()
    {
        AiConfig config = new()
        {
            Enabled = true,
            Provider = AiProviders.Gemini,
            ApiKey = "key",
            Models = new AiModelsConfig { Chat = "gemini-3.8-flash", Fast = "gemini-3.8-flash-lite" }
        };

        await Assert.That(Validate(config).IsValid).IsTrue();
        await Assert.That(config.ResolveChatModel()).IsEqualTo("gemini-3.8-flash");
        await Assert.That(config.ResolveFastModel()).IsEqualTo("gemini-3.8-flash-lite");
    }

    [Test]
    public async Task FastModel_FallsBackToTheChatModel()
    {
        AiConfig config = new() { Models = new AiModelsConfig { Chat = "chat-model" } };

        await Assert.That(config.ResolveChatModel()).IsEqualTo("chat-model");
        await Assert.That(config.ResolveFastModel()).IsEqualTo("chat-model");
    }

    [Test]
    public async Task ReleaseRankingMode_MustBeKnown()
    {
        AiConfig config = new();
        config.Features.ReleaseRanking.Mode = "bogus";

        await Assert.That(Validate(config).IsValid).IsFalse();
    }

    [Test]
    public async Task Limits_CannotBeNegative()
    {
        AiConfig config = new();
        config.Limits.MaxRequestsPerDay = -1;

        await Assert.That(Validate(config).IsValid).IsFalse();
    }

    [Test]
    public async Task Timeout_MustBePositiveAndBounded()
    {
        await Assert.That(Validate(new AiConfig { TimeoutSeconds = 0 }).IsValid).IsFalse();
        await Assert.That(Validate(new AiConfig { TimeoutSeconds = 99999 }).IsValid).IsFalse();
    }

    [Test]
    [Arguments(127, false)]
    [Arguments(128, true)]
    [Arguments(1200, true)]
    [Arguments(8192, true)]
    [Arguments(8193, false)]
    public async Task AssistantOutputTokens_MustBeBounded(int tokens, bool valid)
    {
        AiConfig config = new();
        config.Features.Assistant.MaxOutputTokens = tokens;

        await Assert.That(Validate(config).IsValid).IsEqualTo(valid);
    }
}
