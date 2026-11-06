using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Arrk.Ui;

namespace Arrk.Ui.Tests;

/// <summary>
/// Covers the composition rules the shell relies on: navigation ordering and live availability,
/// region slot ordering and modes, wildcard regions, attribute-discovered contributions and
/// route-collision validation.
/// </summary>
public class UiRegistryTests
{
    sealed class AssemblyProvider(Assembly host, params Assembly[] additional) : IUiAssemblyProvider
    {
        public Assembly HostAssembly { get; } = host;
        public IReadOnlyList<Assembly> AdditionalAssemblies { get; } = additional;
    }

    sealed class FakeNavigationProvider(params NavigationEntry[] entries) : INavigationProvider
    {
        public IReadOnlyList<NavigationEntry> Entries { get; } = entries;
    }

    sealed class FakeSlotProvider(params UiSlot[] slots) : IUiSlotProvider
    {
        public IReadOnlyList<UiSlot> Slots { get; } = slots;
    }

    sealed class FakeDiscoverProvider(params DiscoverSource[] sources) : IDiscoverSourceProvider
    {
        public IReadOnlyList<DiscoverSource> Sources { get; } = sources;
    }

    static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    static UiRegistry Build(
        INavigationProvider[]? navigation = null,
        IUiSlotProvider[]? slots = null,
        IDiscoverSourceProvider[]? discover = null,
        IConfiguration? configuration = null) =>
        new(navigation ?? [], slots ?? [], [], discover ?? [], new AssemblyProvider(typeof(UiRegistry).Assembly), configuration ?? Config());

    [Test]
    public async Task Navigation_IsOrderedWithinSections()
    {
        UiRegistry registry = Build([new FakeNavigationProvider(
            new NavigationEntry(UiSections.Browse, "Later", "/later", "icon", Order: 50),
            new NavigationEntry(UiSections.Browse, "Earlier", "/earlier", "icon", Order: 10),
            new NavigationEntry(UiSections.System, "Admin", "/admin", "icon", Order: 10, AdminOnly: true))]);

        IReadOnlyList<NavigationEntry> browse = registry.NavigationIn(UiSections.Browse);

        await Assert.That(browse.Select(e => e.Label)).IsEquivalentTo(["Earlier", "Later"]);
    }

    [Test]
    public async Task Navigation_HidesEntriesWhoseConfigSectionIsDisabled()
    {
        UiRegistry registry = Build(
            [new FakeNavigationProvider(
                new NavigationEntry(UiSections.Browse, "Movies", "/movies", "icon", EnabledConfigPath: "services:tv"),
                new NavigationEntry(UiSections.Browse, "Music", "/music", "icon", EnabledConfigPath: "services:music"))],
            configuration: Config(("services:tv:enabled", "false")));

        IReadOnlyList<string> hrefs = [.. registry.Navigation.Select(e => e.Href)];

        await Assert.That(hrefs).DoesNotContain("/movies");
        await Assert.That(hrefs).Contains("/music");
    }

    [Test]
    public async Task Navigation_WithNoConfigPath_IsAlwaysAvailable()
    {
        UiRegistry registry = Build([new FakeNavigationProvider(
            new NavigationEntry(UiSections.Browse, "Discover", "/discover", "icon"))]);

        await Assert.That(registry.Navigation.Select(e => e.Href)).Contains("/discover");
    }

    [Test]
    public async Task Slots_AreOrderedPrependBeforeAppend()
    {
        UiRegistry registry = Build(slots: [new FakeSlotProvider(
            new UiSlot("r", typeof(DemoRegionContribution), Order: 1),
            new UiSlot("r", typeof(DemoRegionContribution), Order: 2, Mode: UiSlotMode.Prepend))]);

        IReadOnlyList<UiSlot> slots = registry.SlotsFor("r");

        await Assert.That(slots[0].Mode).IsEqualTo(UiSlotMode.Prepend);
    }

    [Test]
    public async Task ReplaceSlot_ReplacesEverythingElse()
    {
        UiRegistry registry = Build(slots: [new FakeSlotProvider(
            new UiSlot("r", typeof(DemoRegionContribution), Order: 1),
            new UiSlot("r", typeof(DemoRegionContribution), Order: 5, Mode: UiSlotMode.Replace))]);

        IReadOnlyList<UiSlot> slots = registry.SlotsFor("r");

        await Assert.That(slots.Count).IsEqualTo(1);
        await Assert.That(slots[0].Mode).IsEqualTo(UiSlotMode.Replace);
    }

    [Test]
    public async Task DisabledSurface_HidesItsSlots()
    {
        UiRegistry registry = Build(
            slots: [new FakeSlotProvider(
                new UiSlot("r", typeof(DemoRegionContribution), EnabledConfigPath: "services:tv"))],
            configuration: Config(("services:tv:enabled", "false")));

        await Assert.That(registry.SlotsFor("r").Count).IsEqualTo(0);
    }

    [Test]
    public async Task WildcardRegion_MatchesSeveralRegions()
    {
        UiRegistry registry = Build(slots: [new FakeSlotProvider(new UiSlot("tv.*.actions", typeof(DemoRegionContribution)))]);

        await Assert.That(registry.SlotsFor("tv.series.actions").Count).IsEqualTo(1);
        await Assert.That(registry.SlotsFor("tv.movie.actions").Count).IsEqualTo(1);
        await Assert.That(registry.SlotsFor("music.album.actions").Count).IsEqualTo(0);
    }

    [Test]
    public async Task RegionAttribute_IsDiscoveredFromComponentTypes()
    {
        IReadOnlyList<UiSlot> slots = UiRegistry.DiscoverRegionComponents([typeof(DemoRegionContribution)]);

        await Assert.That(slots.Count).IsEqualTo(1);
        await Assert.That(slots[0].Region).IsEqualTo("test.demo");
        await Assert.That(slots[0].Mode).IsEqualTo(UiSlotMode.Prepend);
        await Assert.That(slots[0].Order).IsEqualTo(5);
    }

    [Test]
    public async Task RouteConflicts_AreReported()
    {
        IReadOnlyList<string> conflicts =
            UiRegistry.FindRouteConflicts([typeof(DuplicateRouteOne), typeof(DuplicateRouteTwo), typeof(UniqueRoute)]);

        await Assert.That(conflicts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Registry_Throws_OnDuplicateRoutesAcrossAssemblies()
    {
        await Assert.That(() =>
                new UiRegistry([], [], [], [], new AssemblyProvider(
                    typeof(UiRegistry).Assembly, typeof(UiRegistryTests).Assembly), Config()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DiscoverSources_AreOrderedAndFilteredByAvailability()
    {
        UiRegistry registry = Build(
            discover:
            [
                new FakeDiscoverProvider(
                    new DiscoverSource("music", "Music", "icon", typeof(DemoRegionContribution), Order: 40, EnabledConfigPath: "services:music"),
                    new DiscoverSource("movies", "Movies", "icon", typeof(DemoRegionContribution), Order: 10, EnabledConfigPath: "services:tv"))
            ],
            configuration: Config(("services:tv:enabled", "false")));

        IReadOnlyList<DiscoverSource> sources = registry.DiscoverSources;

        await Assert.That(sources.Count).IsEqualTo(1);
        await Assert.That(sources[0].Id).IsEqualTo("music");
        await Assert.That(registry.FindDiscoverSource("movies")).IsNull();
        await Assert.That(registry.FindDiscoverSource("music")?.Label).IsEqualTo("Music");
    }
}

[UiRegion("test.demo", Order = 5, Mode = UiSlotMode.Prepend)]
public sealed class DemoRegionContribution : ComponentBase;

[Route("test/duplicate")]
public sealed class DuplicateRouteOne : ComponentBase;

[Route("test/duplicate")]
public sealed class DuplicateRouteTwo : ComponentBase;

[Route("test/unique")]
public sealed class UniqueRoute : ComponentBase;
