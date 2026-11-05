using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Ai.Data;
using Stackarr.Core;
using Stackarr.Ui;

namespace Stackarr.Ai;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the optional AI foundation from the <c>ai</c> configuration section. Safe to call
    /// unconditionally: with AI disabled, <see cref="AiService"/> resolves but reports
    /// <see cref="AiService.Enabled"/> as <c>false</c> and no client is built.
    /// </summary>
    public static IServiceCollection AddStackarrAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiConfig>(configuration.GetSection(AiConfig.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        // Constructed explicitly so the optional test/proxy transport parameter never has to be
        // resolved from the container.
        services.AddSingleton(sp => new AiClientFactory(
            sp.GetRequiredService<ILogger<AiClientFactory>>()));

        services.AddSingleton<AiUsageService>();
        services.AddSingleton<AiCache>();
        services.AddSingleton<AiBudget>();
        services.AddSingleton<AiDecisionService>();
        services.AddSingleton<AiPersonaService>();
        services.AddSingleton<AiService>();

        // Assistant runtime. Packs and media sources are contributed by modules; the core packs here
        // are the always-on meta pack and the cross-domain library pack.
        services.AddSingleton<AssistantRateLimiter>();
        services.AddSingleton<AssistantActionRunner>();
        services.AddSingleton<AssistantRegistry>();
        services.AddSingleton<AssistantService>();
        // Only the framework-owned packs live here. Domain modules register their own action and
        // recommendation packs in their ConfigureServices, exactly like media sources.
        services.AddSingleton<IAssistantTool, Tools.MetaAssistantTool>();
        services.AddSingleton<IAssistantTool, Tools.LibraryAssistantTool>();
        services.AddSingleton<IHeadAssetProvider, AiHeadAssets>();
        services.AddSingleton<ITileProvider, AiTileProvider>();

        services.AddSingleton<IValidator<AiConfig>, AiConfigValidator>();

        services.AddSingleton<IModuleEntityConfiguration, AiUsageRecordConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, AiUserPreferenceConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, AiCacheEntryConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, AiDecisionOutcomeConfiguration>();

        services.AddSingleton<IAiFeatureDescriptor>(sp => new BuiltInAiFeature(
            sp.GetRequiredService<IOptionsMonitor<AiConfig>>(),
            AiFeatureKeys.Assistant, "Assistant",
            "In-app chat with tools; on-demand, rate-limited per user.",
            c => c.Features.Assistant.Enabled));
        services.AddSingleton<IAiFeatureDescriptor>(sp => new BuiltInAiFeature(
            sp.GetRequiredService<IOptionsMonitor<AiConfig>>(),
            AiFeatureKeys.ReleaseRanking, "Release ranking",
            "AI tie-breaks between already-eligible releases.",
            c => c.Features.ReleaseRanking.Enabled));
        services.AddSingleton<IAiFeatureDescriptor>(sp => new BuiltInAiFeature(
            sp.GetRequiredService<IOptionsMonitor<AiConfig>>(),
            AiFeatureKeys.ImportMatching, "Local import matching",
            "AI tie-breaks when an uploaded file could belong to more than one title.",
            c => c.Features.ImportMatching.Enabled));
        services.AddSingleton<IAiFeatureDescriptor>(sp => new BuiltInAiFeature(
            sp.GetRequiredService<IOptionsMonitor<AiConfig>>(),
            AiFeatureKeys.Recommendations, "Recommendations",
            "AI re-ranks the deterministic per-user list.",
            c => c.Features.Recommendations.Enabled));
        services.AddSingleton<IAiFeatureDescriptor>(sp => new BuiltInAiFeature(
            sp.GetRequiredService<IOptionsMonitor<AiConfig>>(),
            AiFeatureKeys.Digest, "Digest",
            "Scheduled summary of downloads, requests and lifecycle events.",
            c => c.Features.Digest.Enabled));
        services.AddSingleton<IAiFeatureDescriptor>(sp => new BuiltInAiFeature(
            sp.GetRequiredService<IOptionsMonitor<AiConfig>>(),
            AiFeatureKeys.Style, "Style",
            "Generates per-account custom CSS from a plain-language brief.",
            c => c.Features.Style.Enabled));

        services.AddSingleton<IUserCssAssistant, AiStyleAssistant>();

        return services;
    }
}
