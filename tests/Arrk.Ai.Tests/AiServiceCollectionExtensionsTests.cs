using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Ai;
using Arrk.Core;

namespace Arrk.Ai.Tests;

/// <summary>
/// Proves the DI wiring resolves end-to-end (the optional transport parameter must never be demanded
/// from the container) and that the <c>ai</c> section binds for real.
/// </summary>
public class AiServiceCollectionExtensionsTests
{
    static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContextFactory<ArrkDbContext>(options =>
            options.UseSqlite("Data Source=file:ai-tests?mode=memory&cache=shared"));
        services.AddArrkAi(configuration);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task ResolvesService_WithAiOff_ByDefault()
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>());

        AiService service = provider.GetRequiredService<AiService>();

        await Assert.That(service.Enabled).IsFalse();
    }

    [Test]
    public async Task RegistersTheBuiltInFeatureDescriptors()
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>());

        List<IAiFeatureDescriptor> features = provider.GetServices<IAiFeatureDescriptor>().ToList();

        await Assert.That(features.Select(f => f.Key))
            .IsEquivalentTo(new[] { "assistant", "releaseRanking", "importMatching", "recommendations", "digest", "style" });
    }

    [Test]
    public async Task BindsTheAiSection_AndBuildsAClient_WhenEnabled()
    {
        using ServiceProvider provider = BuildProvider(new()
        {
            ["ai:enabled"] = "true",
            ["ai:provider"] = AiProviders.Ollama,
            ["ai:models:chat"] = "llama3.1:8b"
        });

        AiService service = provider.GetRequiredService<AiService>();

        await Assert.That(service.Enabled).IsTrue();
        await Assert.That(service.Config.Provider).IsEqualTo(AiProviders.Ollama);
        await Assert.That(service.Config.Models.Chat).IsEqualTo("llama3.1:8b");
    }
}
