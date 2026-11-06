using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Ai;
using Arrk.Core;
using Arrk.Core.Modules;
using Arrk.Core.Playback;

namespace Arrk.App.Tests;

/// <summary>
/// The AI module must compose with the real module loader, no domain module has to be present for
/// its services to resolve, and AI being off must still leave a usable service.
/// </summary>
public class AiModuleWiringTests
{
    static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ai:enabled"] = "false"
        }).Build();

    [Test]
    public async Task AiService_ResolvesFromTheRealModuleComposition()
    {
        ModuleLoader loader = ServiceRegistryFactory.CreateModuleLoader();
        ServiceCollection services = new();
        loader.ConfigureAllServices(services, Config());
        services.AddLogging();
        services.AddArrkCore();
        services.AddSingleton(Config());
        services.AddSingleton(new ArrkInstance(Config()));
        services.AddSingleton(loader.BuildRegistry());
        services.AddSingleton(new Arrk.Navidrome.NavidromeApiSecret());
        services.AddDbContextFactory<ArrkDbContext>(options =>
            options.UseSqlite("Data Source=file:ai-wiring?mode=memory&cache=shared"));

        // Registrations are what the host composes; the assistant runtime itself is exercised in
        // Arrk.Ai.Tests, where sources and packs are supplied without the full container graph.
        await Assert.That(services.Any(d => d.ServiceType == typeof(AssistantService))).IsTrue();
        await Assert.That(services.Count(d => d.ServiceType == typeof(IAssistantMediaSource))).IsEqualTo(2);
        await Assert.That(services.Count(d => d.ServiceType == typeof(IAssistantMediaActionSource))).IsEqualTo(2);
        await Assert.That(services.Count(d => d.ServiceType == typeof(IAssistantRecommendationSource))).IsEqualTo(2);
        await Assert.That(services.Count(d => d.ServiceType == typeof(IAssistantTool))).IsEqualTo(10);

        await using ServiceProvider provider = services.BuildServiceProvider();

        AiService ai = provider.GetRequiredService<AiService>();

        await Assert.That(ai).IsNotNull();
        await Assert.That(ai.Enabled).IsFalse();
        await Assert.That(provider.GetServices<IAiFeatureDescriptor>().Count()).IsEqualTo(6);

        // The real module graph must construct every pack, action runner and domain source; a missing
        // dependency here would only surface when a user opens the popup.
        AssistantService assistant = provider.GetRequiredService<AssistantService>();
        AssistantActionRunner runner = provider.GetRequiredService<AssistantActionRunner>();
        await Assert.That(assistant).IsNotNull();
        await Assert.That(runner).IsNotNull();

        foreach (IAssistantTool tool in provider.GetServices<IAssistantTool>())
            await Assert.That(tool.Name).IsNotEmpty();
        foreach (IAssistantMediaSource source in provider.GetServices<IAssistantMediaSource>())
            await Assert.That(source.Key).IsNotEmpty();
        foreach (IAssistantMediaActionSource source in provider.GetServices<IAssistantMediaActionSource>())
            await Assert.That(source.SupportedActions.Count).IsGreaterThan(0);
        foreach (IAssistantRecommendationSource source in provider.GetServices<IAssistantRecommendationSource>())
            await Assert.That(source.Key).IsNotEmpty();

    }
}
