using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Ai;
using Stackarr.Ai.Tools;
using Stackarr.TestSupport;

namespace Stackarr.Ai.Tests;

/// <summary>
/// Persona selection and the knowledge policy. Built-ins are only plain/Jarvis/Cortana; the
/// character additions (Johnson, Emilia, Coconut) are ordinary config entries. The prompt must allow
/// general knowledge and opinions while keeping library facts tool-grounded.
/// </summary>
public class AiPersonasTests
{
    static AssistantToolContext Context() => new(
        1, "alice", false, new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    [Test]
    public async Task BuiltIns_AreOnlyPlainJarvisAndCortana()
    {
        await Assert.That(AiPersonas.BuiltIn.Select(p => p.Key).ToArray())
            .IsEquivalentTo(new[] { "plain", "jarvis", "cortana" });
    }

    [Test]
    public async Task DefaultConfig_ShipsJohnsonEmiliaAndCoconutAsCustom()
    {
        AiConfig config = new();

        await Assert.That(AiPersonas.All(config).Select(p => p.Key).ToArray()).IsEquivalentTo(
            new[] { "plain", "jarvis", "cortana", "johnson", "emilia", "coconut" });
        await Assert.That(AiPersonas.Resolve(config, null).Key).IsEqualTo("plain");
        await Assert.That(AiPersonas.Resolve(config, "emilia").Name).IsEqualTo("Emilia");
    }

    [Test]
    public async Task CustomPersona_CanBeAddedAndCannotShadowABuiltIn()
    {
        AiConfig config = new()
        {
            Personas = new AiPersonasConfig
            {
                Default = "captain",
                Custom =
                [
                    new AiPersonaDefinition { Key = "captain", Name = "Captain", Style = "Speak like a starship captain." },
                    new AiPersonaDefinition { Key = "jarvis", Name = "Impostor", Style = "Nope." }
                ]
            }
        };

        IReadOnlyList<AssistantPersona> personas = AiPersonas.All(config);
        await Assert.That(personas.Any(p => p.Key == "captain")).IsTrue();
        await Assert.That(personas.Single(p => p.Key == "jarvis").Name).IsEqualTo("Jarvis");
        await Assert.That(AiPersonas.Resolve(config, null).Key).IsEqualTo("captain");
    }

    [Test]
    public async Task Validator_RejectsADefaultThatDoesNotExist()
    {
        AiConfig config = new() { Personas = new AiPersonasConfig { Default = "nobody" } };
        FluentValidation.Results.ValidationResult result = new AiConfigValidator().Validate(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Prompt_PermitsGeneralKnowledge_AndIncludesThePersona()
    {
        AiConfig config = new();
        AssistantRegistry registry = new([Meta(config)], []);
        AssistantPersona persona = AiPersonas.Resolve(config, "jarvis");

        string prompt = AssistantPrompt.Build(
            config, new AssistantUser(1, "alice", false), registry, Context(),
            new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), null, persona);

        await Assert.That(prompt).Contains("may use your own knowledge for opinions");
        await Assert.That(prompt).Contains("Persona: Jarvis");
        await Assert.That(prompt).Contains("Tone only");
        await Assert.That(prompt).Contains("Library, download, request, watch-history and system facts must come from a tool");
    }

    [Test]
    public async Task Prompt_StaysCompact()
    {
        AiConfig config = new();
        AssistantRegistry registry = new([Meta(config)], []);

        string prompt = AssistantPrompt.Build(
            config, new AssistantUser(1, "alice", false), registry, Context(),
            new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), null,
            AiPersonas.Resolve(config, "plain"));

        // The prompt is re-sent on every tool round; keep it from growing back into a page.
        await Assert.That(prompt.Length).IsLessThan(2_000);
    }

    static MetaAssistantTool Meta(AiConfig config)
    {
        AiService ai = new(
            new AiClientFactory(NullLogger<AiClientFactory>.Instance),
            new MutableOptionsMonitor<AiConfig>(config),
            NullLogger<AiService>.Instance);
        return new MetaAssistantTool(ai, [], [], []);
    }
}
