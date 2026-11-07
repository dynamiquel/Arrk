namespace Arrk.Ai.Tests;

/// <summary>
/// Optional live smoke test against a real OpenAI-compatible provider, using the same configuration
/// shape the app uses. It only runs when <c>ARRK_LIVE_AI_KEY</c> is set, so CI stays hermetic.
/// </summary>
public class AssistantLiveSmokeTests
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
    public async Task LiveQuestion_StreamsAnAnswer()
    {
        AiConfig? config = LiveConfig();
        if (config is null)
            return;

        using AssistantTestHost host = new(config);
        AssistantService service = host.CreateService();

        List<AssistantUpdate> updates = [];
        await foreach (AssistantUpdate update in service.StreamAsync(
                           AssistantTestHost.User(), "Reply with exactly: READY"))
        {
            updates.Add(update);
        }

        await Assert.That(updates.OfType<AssistantFailed>().Count()).IsEqualTo(0);
        string answer = string.Concat(updates.OfType<AssistantTextDelta>().Select(u => u.Text));
        await Assert.That(answer.Length).IsGreaterThan(0);
    }
}
