using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Prowlarr.Configuration;
using Arrk.Bazarr;
using Arrk.Subtitles.Abstractions;
using Arrk.Subtitles.OpenSubtitles;
using Arrk.Jellyfin;
using Arrk.Music;
using Arrk.Navidrome;
using Arrk.Tvarr;

namespace Arrk.Orchestration.Tests;

/// <summary>
/// End-to-end coverage of the schema-driven visual config editor against the full
/// set of registered modules: render defaults, build the form, serialize it back
/// and validate with the exact same validators the YAML editor uses.
/// </summary>
public class ConfigFormIntegrationTests
{
    static ConfigSectionRegistry BuildFullRegistry()
    {
        ConfigSectionRegistry registry = new();
        ConfigDefaults.RegisterCoreSections(registry);
        registry.Register(new ConfigSectionProvider<TvConfig>("services:tv", "TV", icon: "Tv", group: "Services", order: 10, groupOrder: 20));
        registry.Register(new ConfigSectionProvider<MusicConfig>("services:music", "Music", icon: "MusicNote", group: "Services", order: 20, groupOrder: 20));
        registry.Register(new ConfigSectionProvider<JellyfinConfig>("services:jellyfin", "Jellyfin", icon: "PlayCircle", group: "Services", order: 60, groupOrder: 20));
        registry.Register(new ConfigSectionProvider<NavidromeConfig>("services:navidrome", "Navidrome", icon: "MusicNote", group: "Services", order: 70, groupOrder: 20));
        registry.Register(new ConfigSectionProvider<TorrentDownloaderConfig>("services:torrentDownloader", "Downloads", icon: "Download", group: "Services", order: 40, groupOrder: 20));
        registry.Register(new ConfigSectionProvider<SubtitlesConfig>("services:subtitles", "Subtitles", icon: "Subtitles", group: "Services", order: 50, groupOrder: 20));
        registry.Register(new ConfigSectionProvider<OpenSubtitlesConfig>("services:subtitles:opensubtitles", "OpenSubtitles", icon: "Subtitles", group: "Services", order: 51, groupOrder: 20));
        registry.Register(new ConfigSectionProvider<BazarrConfig>("services:subtitles:bazarr", "Bazarr", icon: "Subtitles", group: "Services", order: 52, groupOrder: 20));
        registry.Register(new ConfigSectionProvider<ProwlarrConfig>("indexers", "Indexers", icon: "TravelExplore", group: "Infrastructure", order: 70, groupOrder: 10));
        return registry;
    }

    static (ConfigFormBuilder Builder, ConfigValidator Validator, ConfigSectionRegistry Registry) Create()
    {
        ConfigSectionRegistry registry = BuildFullRegistry();

        ServiceCollection services = new();
        services.AddArrkConfiguration();
        ServiceProvider provider = services.BuildServiceProvider();

        ConfigSchemaGenerator generator = new(registry, provider);
        return (new ConfigFormBuilder(generator, registry), new ConfigValidator(generator, registry, provider), registry);
    }

    [Test]
    public async Task NestedProviderSections_RenderAndBindUnderSubtitles()
    {
        (ConfigFormBuilder builder, _, ConfigSectionRegistry registry) = Create();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:subtitles:opensubtitles:apiKey"] = "secret",
                ["services:subtitles:bazarr:enabled"] = "true"
            })
            .Build();

        string yaml = ConfigRenderer.Render(config, registry);

        await Assert.That(yaml).Contains("opensubtitles:");
        await Assert.That(yaml).Contains("bazarr:");
        await Assert.That(yaml).DoesNotContain("subtitles:opensubtitles");
        await Assert.That(yaml).DoesNotContain("subtitles:bazarr");

        ConfigFormModel model = builder.Build(yaml);
        ConfigFormSection? openSubtitles = model.FindSection("services:subtitles:opensubtitles");
        ConfigFormSection? bazarr = model.FindSection("services:subtitles:bazarr");

        await Assert.That(openSubtitles).IsNotNull();
        await Assert.That(bazarr).IsNotNull();
        ConfigScalarNode? apiKey = openSubtitles!.Children
            .OfType<ConfigScalarNode>()
            .FirstOrDefault(node => node.Key == "apiKey");
        ConfigScalarNode? enabled = bazarr!.Children
            .OfType<ConfigScalarNode>()
            .FirstOrDefault(node => node.Key == "enabled");

        await Assert.That(apiKey?.Value).IsEqualTo("secret");
        await Assert.That(enabled?.Value).IsEqualTo(true);

        await Assert.That(model.TopLevelSections.Select(section => section.SectionPath))
            .Contains("services:subtitles");
        await Assert.That(model.TopLevelSections.Select(section => section.SectionPath))
            .DoesNotContain("services:subtitles:opensubtitles");
        await Assert.That(model.TopLevelSections.Select(section => section.SectionPath))
            .DoesNotContain("services:subtitles:bazarr");
    }

    [Test]
    public async Task NestedProviderEdit_SerializesIntoTheParentSection()
    {
        (ConfigFormBuilder builder, _, ConfigSectionRegistry registry) = Create();
        string effective = ConfigRenderer.Render(new ConfigurationBuilder().Build(), registry);
        ConfigFormModel model = builder.Build(effective);
        ConfigFormSection subtitles = model.FindSection("services:subtitles")!;
        ConfigObjectNode openSubtitles = subtitles.Children
            .OfType<ConfigObjectNode>()
            .Single(node => node.Key == "opensubtitles");
        ConfigScalarNode apiKey = openSubtitles.Children
            .OfType<ConfigScalarNode>()
            .Single(node => node.Key == "apiKey");
        apiKey.Value = "new-key";

        string yaml = ConfigFormSerializer.Serialize(model, effective);
        Dictionary<string, object?> root = ConfigFormBuilder.ParseYaml(yaml);
        Dictionary<string, object?> services = (Dictionary<string, object?>)root["services"]!;
        Dictionary<string, object?> subtitlesValue = (Dictionary<string, object?>)services["subtitles"]!;
        Dictionary<string, object?> openSubtitlesValue =
            (Dictionary<string, object?>)subtitlesValue["opensubtitles"]!;

        await Assert.That(openSubtitlesValue["apiKey"]).IsEqualTo("new-key");
    }

    [Test]
    public async Task FullRegistry_DefaultForm_RoundTripsAndValidates()
    {
        (ConfigFormBuilder builder, ConfigValidator validator, ConfigSectionRegistry registry) = Create();

        IConfiguration empty = new ConfigurationBuilder().Build();
        string effective = ConfigRenderer.Render(empty, registry);

        ConfigFormModel model = builder.Build(effective);
        string yaml = ConfigFormSerializer.Serialize(model);

        await Assert.That(validator.ValidateWithSchema(yaml)).IsEmpty();
        await Assert.That(validator.ValidateWithFluentValidation(yaml).IsValid).IsTrue();
    }

    [Test]
    public async Task FullRegistry_IndexerUntypedSettings_ArePreserved()
    {
        (ConfigFormBuilder builder, ConfigValidator validator, ConfigSectionRegistry registry) = Create();

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["indexers:entries:0:name"] = "1337x",
                ["indexers:entries:0:implementation"] = "thepiratebay",
                ["indexers:entries:0:settings:username"] = "bob",
                ["indexers:entries:0:settings:password"] = "secret"
            })
            .Build();

        string effective = ConfigRenderer.Render(config, registry);
        ConfigFormModel model = builder.Build(effective);
        string yaml = ConfigFormSerializer.Serialize(model);

        await Assert.That(yaml).Contains("username: bob");
        await Assert.That(yaml).Contains("password: secret");
        await Assert.That(validator.ValidateWithSchema(yaml)).IsEmpty();
    }

    [Test]
    public async Task FullRegistry_FormGroupsComeFromModuleMetadata()
    {
        (ConfigFormBuilder builder, _, ConfigSectionRegistry registry) = Create();

        ConfigFormModel model = builder.Build(ConfigRenderer.Render(new ConfigurationBuilder().Build(), registry));

        await Assert.That(model.Groups.Select(g => g.Title)).Contains("Infrastructure");
        await Assert.That(model.Groups.Select(g => g.Title)).Contains("Services");

        ConfigFormSection jellyfin = model.FindSection("services:jellyfin")!;
        await Assert.That(jellyfin.Icon).IsEqualTo("PlayCircle");
        await Assert.That(jellyfin.Group).IsEqualTo("Services");
    }

    [Test]
    public async Task FullRegistry_FormSave_OmitsUnchangedDefaults()
    {
        (ConfigFormBuilder builder, _, ConfigSectionRegistry registry) = Create();

        IConfiguration empty = new ConfigurationBuilder().Build();
        string defaults = ConfigRenderer.Render(empty, registry);

        ConfigFormModel model = builder.Build(defaults);
        ConfigScalarNode cleanup = (ConfigScalarNode)FindNode(model, "services:torrentDownloader", "management", "storage", "cleanupStrategy")!;
        cleanup.Value = "newest";

        string yaml = ConfigFormSerializer.Serialize(model, null, defaults);
        Dictionary<string, object?> parsed = ConfigFormBuilder.ParseYaml(yaml);

        await Assert.That(yaml).Contains("cleanupStrategy: newest");
        await Assert.That(parsed.ContainsKey("services")).IsTrue();
        // Everything left at its default is not written.
        await Assert.That(parsed.ContainsKey("metadata")).IsFalse();
        await Assert.That(parsed.ContainsKey("storage")).IsFalse();
        await Assert.That(parsed.ContainsKey("notifications")).IsFalse();
        await Assert.That(parsed.ContainsKey("indexers")).IsFalse();
    }

    static ConfigFormNode? FindNode(ConfigFormModel model, string sectionPath, params string[] path)
    {
        ConfigFormNode current = model.FindSection(sectionPath)!;
        foreach (string segment in path)
        {
            if (current is not ConfigObjectNode obj)
                return null;
            current = obj.Children.FirstOrDefault(c => c.Key.Equals(segment, StringComparison.OrdinalIgnoreCase))!;
        }
        return current;
    }
}
