using Arrk.Ai;
using Arrk.Ui;

namespace Arrk.Ai.Tests;

/// <summary>
/// The AI style helper is advisory: it must hand back plain CSS even when the model wraps it in a
/// markdown fence or a language label, because the Account editor stores the text verbatim.
/// </summary>
public class AiStyleAssistantTests
{
    [Test]
    public async Task Clean_StripsMarkdownFences()
    {
        string? css = AiStyleAssistant.Clean("""
            ```css
            :root { --st-pink: #f0f; }
            ```
            """);

        await Assert.That(css).IsEqualTo(":root { --st-pink: #f0f; }");
    }

    [Test]
    public async Task Clean_StripsALanguageLabel()
    {
        string? css = AiStyleAssistant.Clean("css: .st-stat-card { border-radius: 4px; }");

        await Assert.That(css).IsEqualTo(".st-stat-card { border-radius: 4px; }");
    }

    [Test]
    public async Task Clean_ReturnsNullForAnEmptyAnswer()
    {
        await Assert.That(AiStyleAssistant.Clean("   ")).IsNull();
    }

    [Test]
    public async Task Prompt_ExplainsTheDerivedTokenLayers()
    {
        string prompt = AiStyleAssistant.BuildPrompt(new UserCssRequest(
            1,
            "make it white with blue and green highlights",
            ":root { --st-pink: #0ea5e9; }"));

        await Assert.That(prompt).Contains("--st-pink");
        await Assert.That(prompt).Contains("--st-purple");
        await Assert.That(prompt).Contains("--st-blue");
        await Assert.That(prompt).Contains("--st-glass-surface");
        await Assert.That(prompt).Contains("--st-overlay-fill");
        await Assert.That(prompt).Contains(".mud-dialog");
        await Assert.That(prompt).Contains(".mud-overlay-scrim");
        await Assert.That(prompt).Contains("make it white with blue and green highlights");
        await Assert.That(prompt).Contains("--st-pink: #0ea5e9;");
        await Assert.That(prompt).DoesNotContain("```");
    }
}
