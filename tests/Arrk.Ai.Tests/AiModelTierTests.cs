using System.Net;
using System.Text;
using System.Text.Json;
using Arrk.Ai.Data;
using Arrk.Core.Configuration;
using Arrk.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arrk.Ai.Tests;

public class AiModelTierTests
{
    static AiConfig Config() => new()
    {
        Enabled = true,
        Provider = AiProviders.Ollama,
        Models = new AiModelsConfig { Low = "low-model", Medium = "medium-model", High = "high-model" }
    };

    [Test]
    [Arguments("Medium", 0)]
    [Arguments("Low", 1)]
    [Arguments("High", 2)]
    public async Task NumericRoles_AreStable(string name, int expected)
    {
        AiModelRole role = Enum.Parse<AiModelRole>(name);
        await Assert.That((int)role).IsEqualTo(expected);
    }

    [Test]
    public async Task RemovedModelKeys_AreIgnored()
    {
        AiConfig config = YamlConfigSerializer.Deserialize<AiConfig>("""
            enabled: true
            provider: ollama
            models:
              chat: ' legacy-model '
              fast: ' legacy-fast '
            """)!;

        await Assert.That(config.ResolveMediumModel()).IsEqualTo("");
        await Assert.That(config.ResolveLowModel()).IsEqualTo("");
        await Assert.That(new AiConfigValidator().Validate(config).IsValid).IsFalse();
    }

    [Test]
    public async Task Low_FallsBackToMedium()
    {
        AiConfig config = Config();
        config.Models.Low = " ";

        await Assert.That(config.ResolveLowModel()).IsEqualTo("medium-model");
    }

    [Test]
    public async Task MediumOnly_IsValid_ButLowAndHighDoNotReplaceRequiredMedium()
    {
        AiConfig config = Config();
        config.Models.Low = "";
        config.Models.High = "";
        await Assert.That(new AiConfigValidator().Validate(config).IsValid).IsTrue();

        config.Models = new AiModelsConfig { Low = "low", High = "high" };
        await Assert.That(new AiConfigValidator().Validate(config).IsValid).IsFalse();
    }

    [Test]
    [Arguments(AiModelRole.Low, "low-model")]
    [Arguments(AiModelRole.Medium, "medium-model")]
    [Arguments(AiModelRole.High, "high-model")]
    public async Task RequestsAndUsage_SelectTheSameTier(AiModelRole role, string expectedModel)
    {
        using TestAiDb db = new();
        AiUsageService usage = new(db, TimeProvider.System);
        FakeHttpMessageHandler transport = new();
        transport.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""
            {"id":"x","object":"chat.completion","created":1,"model":"m",
             "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
            """));
        using AiService ai = new(
            new AiClientFactory(NullLogger<AiClientFactory>.Instance, transport),
            new MutableOptionsMonitor<AiConfig>(Config()),
            NullLogger<AiService>.Instance, usage);

        string? answer = await ai.AskAsync("hello", role, AiFeatureKeys.Core, "tier-test");

        await Assert.That(answer).IsEqualTo("ok");
        using JsonDocument body = JsonDocument.Parse(transport.Requests.Single().Body);
        await Assert.That(body.RootElement.GetProperty("model").GetString()).IsEqualTo(expectedModel);
        IReadOnlyList<AiUsageRecord> records = await usage.GetRecentAsync();
        await Assert.That(records.Single().Model).IsEqualTo(expectedModel);
    }

    [Test]
    public async Task UnsetTiers_ShareMediumClient_AndReloadReplacesAllThree()
    {
        AiConfig config = Config();
        config.Models.Low = " ";
        config.Models.High = "";
        MutableOptionsMonitor<AiConfig> monitor = new(config);
        using AiService ai = new(new AiClientFactory(NullLogger<AiClientFactory>.Instance),
            monitor, NullLogger<AiService>.Instance);

        await Assert.That(ReferenceEquals(ai.MediumClient, ai.LowClient)).IsTrue();
        await Assert.That(ReferenceEquals(ai.MediumClient, ai.HighClient)).IsTrue();
        await Assert.That(ReferenceEquals(ai.MediumClient, ai.MediumClient)).IsTrue();

        IChatClient? previous = ai.MediumClient;
        monitor.Update(Config());

        await Assert.That(ReferenceEquals(ai.MediumClient, previous)).IsFalse();
        await Assert.That(ReferenceEquals(ai.MediumClient, ai.LowClient)).IsFalse();
        await Assert.That(ReferenceEquals(ai.MediumClient, ai.HighClient)).IsFalse();
        await Assert.That(ReferenceEquals(ai.ClientFor(AiModelRole.High), ai.HighClient)).IsTrue();

        monitor.Update(new AiConfig());
        await Assert.That(ai.Enabled).IsFalse();
        await Assert.That(ai.MediumClient).IsNull();
        await Assert.That(ai.LowClient).IsNull();
        await Assert.That(ai.HighClient).IsNull();
    }

    [Test]
    public async Task Assistant_DefaultsToMedium()
    {
        using AssistantTestHost host = new(Config());
        AssistantService service = host.CreateService(new ScriptedChatClient().ThenAnswer("hello"));

        await foreach (AssistantUpdate update in service.StreamAsync(AssistantTestHost.User(), "hi"))
        {
            await Assert.That(update is AssistantFailed).IsFalse();
        }

        IReadOnlyList<AiUsageRecord> records = await host.Usage.GetRecentAsync();
        await Assert.That(records.Single().Model).IsEqualTo("medium-model");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Assistant_UsesMedium_AndRateLimitsFallBackToLow(bool rateLimit)
    {
        using AssistantTestHost host = new(Config());
        FakeHttpMessageHandler transport = new();
        transport.Fallback = async (request, ct) =>
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            string? model = body.RootElement.GetProperty("model").GetString();
            if (rateLimit && model != "low-model")
                return FakeHttpMessageHandler.JsonResponse(
                    """{"error":{"message":"rate limited","type":"rate_limit_error"}}""",
                    HttpStatusCode.TooManyRequests);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"id":"x","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}

                    data: {"id":"x","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]


                    """, Encoding.UTF8, "text/event-stream")
            };
        };
        using AiService ai = new(new AiClientFactory(NullLogger<AiClientFactory>.Instance, transport),
            host.Monitor, NullLogger<AiService>.Instance, host.Usage, host.Budget);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        AssistantService service = new(ai, host.Registry, host.Limiter, host.Usage, host.Budget,
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AssistantService>.Instance, host.Time);
        List<AssistantUpdate> updates = [];

        await foreach (AssistantUpdate update in service.StreamAsync(AssistantTestHost.User(), "hi"))
            updates.Add(update);

        await Assert.That(updates.OfType<AssistantFailed>().Any()).IsFalse();
        await Assert.That(string.Concat(updates.OfType<AssistantTextDelta>().Select(delta => delta.Text)))
            .IsEqualTo("hello");
        using JsonDocument first = JsonDocument.Parse(transport.Requests[0].Body);
        await Assert.That(first.RootElement.GetProperty("model").GetString()).IsEqualTo("medium-model");
        using JsonDocument last = JsonDocument.Parse(transport.Requests[^1].Body);
        string expected = rateLimit ? "low-model" : "medium-model";
        await Assert.That(last.RootElement.GetProperty("model").GetString()).IsEqualTo(expected);
        IReadOnlyList<AiUsageRecord> records = await host.Usage.GetRecentAsync();
        await Assert.That(records.Single().Model).IsEqualTo(expected);
    }
}
