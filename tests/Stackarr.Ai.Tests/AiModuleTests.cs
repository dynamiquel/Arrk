using Stackarr.Core.Configuration;
using Stackarr.TestSupport;
using Stackarr.Ui;

namespace Stackarr.Ai.Tests;

/// <summary>
/// The AI module owns its config section and contributes its navigation/jobs like any other
/// module, while contributing no Docker service.
/// </summary>
public class AiModuleTests
{
    [Test]
    public async Task ExposesNoDockerService()
    {
        AiModule module = new();
        await Assert.That(module.GetServices()).IsEmpty();
    }

    [Test]
    public async Task OwnsTheAiConfigSection()
    {
        AiModule module = new();
        IConfigSectionProvider section = module.GetConfigSections().Single();

        await Assert.That(section.SectionPath).IsEqualTo("ai");
        await Assert.That(section.ConfigType).IsEqualTo(typeof(AiConfig));
        await Assert.That(section.IsUserEditable).IsTrue();
    }

    [Test]
    public async Task Navigation_IsAdminOnly()
    {
        NavigationEntry entry = new AiNavigation().Entries.Single();

        await Assert.That(entry.Href).IsEqualTo("/ai");
        await Assert.That(entry.AdminOnly).IsTrue();
    }

    [Test]
    public async Task FeatureDescriptor_HonoursTheMasterSwitchAndItsOwnToggle()
    {
        MutableOptionsMonitor<AiConfig> monitor = new(new AiConfig
        {
            Enabled = true,
            Features = new AiFeaturesConfig
            {
                ReleaseRanking = new AiReleaseRankingFeature { Enabled = false }
            }
        });
        BuiltInAiFeature feature = new(
            monitor,
            AiFeatureKeys.ReleaseRanking,
            "Release ranking",
            "",
            c => c.Features.ReleaseRanking.Enabled);

        await Assert.That(feature.IsEnabled).IsFalse();

        monitor.Update(new AiConfig
        {
            Enabled = true,
            Features = new AiFeaturesConfig
            {
                ReleaseRanking = new AiReleaseRankingFeature { Enabled = true }
            }
        });

        await Assert.That(feature.IsEnabled).IsTrue();
    }
}
