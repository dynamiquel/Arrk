using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core;
using Arrk.Core.Configuration;

namespace Arrk.Configuration.Tests;

/// <summary>
/// Covers the schema-driven visual config editor: form building from the same JSON
/// schema the YAML editor uses, serialization back to YAML, and validation parity.
/// </summary>
public class ConfigFormTests
{
    static (ConfigFormBuilder Builder, ConfigValidator Validator, ConfigSectionRegistry Registry) Create()
    {
        ConfigSectionRegistry registry = new();
        ConfigDefaults.RegisterCoreSections(registry);
        registry.Register(new ConfigSectionProvider<TorrentDownloaderConfig>("services:torrentDownloader", "Downloads",
            icon: "Download", group: "Services", order: 40, groupOrder: 20));

        ServiceCollection services = new();
        services.AddSingleton<IValidator<ArrkConfig>, ArrkConfigValidator>();
        services.AddSingleton<IValidator<StorageConfig>, StorageConfigValidator>();
        services.AddSingleton<IValidator<VpnConfig>, VpnConfigValidator>();
        services.AddSingleton<IValidator<TorrentDownloaderConfig>, TorrentDownloaderConfigValidator>();
        ServiceProvider provider = services.BuildServiceProvider();

        ConfigSchemaGenerator generator = new(registry, provider);
        return (new ConfigFormBuilder(generator, registry), new ConfigValidator(generator, registry, provider), registry);
    }

    const string SampleYaml = """
        storage:
          media: /mnt/media
          downloads: /mnt/downloads
        vpn:
          enabled: true
          type: wireguard
          wireguardPrivateKey: secret-key
          wireguardAddresses: 10.0.0.1/32
        routes:
          customRoutes:
            jellyfin: /media
        services:
          torrentDownloader:
            seeding:
              maxRatio: 2.5
            management:
              storage:
                cleanupStrategy: newest
        """;

    [Test]
    public async Task Build_GroupsSectionsIntoApplicationAndServices()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);

        await Assert.That(model.Groups).IsNotEmpty();
        await Assert.That(model.FindSection("storage")).IsNotNull();
        await Assert.That(model.FindSection("vpn")).IsNotNull();

        ConfigFormSection downloads = model.FindSection("services:torrentDownloader")!;
        await Assert.That(downloads).IsNotNull();
        await Assert.That(downloads.IsService).IsTrue();
        await Assert.That(downloads.Title).IsEqualTo("Downloads");
    }

    [Test]
    public async Task Build_SectionUiMetadata_ComesFromProvider()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        ConfigFormSection downloads = model.FindSection("services:torrentDownloader")!;

        await Assert.That(downloads.Icon).IsEqualTo("Download");
        await Assert.That(downloads.Group).IsEqualTo("Services");

        ConfigFormGroup servicesGroup = model.Groups.First(g => g.Title == "Services");
        await Assert.That(servicesGroup.Sections).Contains(downloads);
    }

    [Test]
    public async Task Build_ScalarValues_ComeFromEffectiveYaml()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        ConfigScalarNode maxRatio = FindScalar(model, "services:torrentDownloader", "seeding", "maxRatio")!;

        await Assert.That(Convert.ToDouble(maxRatio.Value)).IsEqualTo(2.5);
    }

    [Test]
    public async Task Build_EnumProperty_ExposesAllowedValues()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        ConfigScalarNode type = FindScalar(model, "vpn", "type")!;

        await Assert.That(type.FieldType).IsEqualTo(ConfigFormFieldType.Enum);
        await Assert.That(type.EnumValues).Contains("wireguard");
        await Assert.That(type.EnumValues).Contains("openvpn");
    }

    [Test]
    public async Task Build_AllowedValuesAttribute_BecomesEnum()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        ConfigScalarNode strategy = FindScalar(model, "services:torrentDownloader", "management", "storage", "cleanupStrategy")!;

        await Assert.That(strategy.FieldType).IsEqualTo(ConfigFormFieldType.Enum);
        await Assert.That(strategy.EnumValues).Contains("oldest");
        await Assert.That(strategy.EnumValues).Contains("newest");
        await Assert.That(strategy.EnumValues).Contains("least_ratio");
    }

    [Test]
    public async Task Build_SecretProperty_IsMarked()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        ConfigScalarNode key = FindScalar(model, "vpn", "wireguardPrivateKey")!;

        await Assert.That(key.IsSecret).IsTrue();
        await Assert.That(key.Format).IsEqualTo("password");
    }

    [Test]
    public async Task Build_DictionaryProperty_PreservesEntries()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        ConfigDictionaryNode? customRoutes = FindNode(model, "routes", "customRoutes") as ConfigDictionaryNode;

        await Assert.That(customRoutes).IsNotNull();
        await Assert.That(customRoutes!.Entries.Count).IsEqualTo(1);
        await Assert.That(customRoutes.Entries[0].Key).IsEqualTo("jellyfin");
        await Assert.That(customRoutes.Entries[0].Value).IsTypeOf<ConfigScalarNode>();
    }

    [Test]
    public async Task Build_MissingValues_FallBackToSchemaDefaults()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build("");
        ConfigScalarNode maxRatio = FindScalar(model, "services:torrentDownloader", "seeding", "maxRatio")!;

        await Assert.That(Convert.ToDouble(maxRatio.Value)).IsEqualTo(2.0);
    }

    [Test]
    public async Task Serialize_RoundTrips_NestedServicesAndDictionaries()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        string yaml = ConfigFormSerializer.Serialize(model);

        Dictionary<string, object?> parsed = ConfigFormBuilder.ParseYaml(yaml);

        await Assert.That(parsed.ContainsKey("storage")).IsTrue();
        await Assert.That(parsed.ContainsKey("services")).IsTrue();

        Dictionary<string, object?> services = (Dictionary<string, object?>)parsed["services"]!;
        await Assert.That(services.ContainsKey("torrentDownloader")).IsTrue();

        Dictionary<string, object?> routes = (Dictionary<string, object?>)parsed["routes"]!;
        Dictionary<string, object?> customRoutes = (Dictionary<string, object?>)routes["customRoutes"]!;
        await Assert.That(customRoutes["jellyfin"]).IsEqualTo("/media");
    }

    [Test]
    public async Task Serialize_Output_ValidatesWithFluentValidation()
    {
        (ConfigFormBuilder builder, ConfigValidator validator, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        string yaml = ConfigFormSerializer.Serialize(model);

        ConfigValidationResult result = validator.ValidateWithFluentValidation(yaml);

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Errors).IsEmpty();
    }

    [Test]
    public async Task Serialize_AfterEnumEdit_ProducesValidYaml()
    {
        (ConfigFormBuilder builder, ConfigValidator validator, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        ConfigScalarNode strategy = FindScalar(model, "services:torrentDownloader", "management", "storage", "cleanupStrategy")!;
        strategy.Value = "oldest";

        string yaml = ConfigFormSerializer.Serialize(model);
        ConfigValidationResult result = validator.ValidateWithFluentValidation(yaml);

        await Assert.That(yaml).Contains("cleanupStrategy: oldest");
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Serialize_PreservesUnknownRootKeys()
    {
        (ConfigFormBuilder builder, _, _) = Create();

        ConfigFormModel model = builder.Build(SampleYaml);
        string yaml = ConfigFormSerializer.Serialize(model, "customTopLevel:\n  foo: bar\n");

        await Assert.That(yaml).Contains("customTopLevel");
        await Assert.That(yaml).Contains("foo: bar");
    }

    [Test]
    public async Task Validation_ServiceSection_IsActuallyValidated()
    {
        (_, ConfigValidator validator, _) = Create();

        ConfigValidationResult result = validator.ValidateWithFluentValidation("""
            services:
              torrentDownloader:
                management:
                  storage:
                    cleanupStrategy: bogus
            """);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.Contains("cleanupStrategy", StringComparison.OrdinalIgnoreCase))).IsTrue();
        await Assert.That(result.Details.Any(d => d.Path == "services.torrentDownloader.management.storage.cleanupStrategy")).IsTrue();
    }

    [Test]
    public async Task ValidateWithSchema_ServiceSection_IsNotUnknownProperty()
    {
        (_, ConfigValidator validator, _) = Create();

        List<string> errors = validator.ValidateWithSchema("""
            services:
              torrentDownloader:
                seeding:
                  maxRatio: 2.0
            """);

        await Assert.That(errors.Any(e => e.Contains("unknown property"))).IsFalse();
    }

    [Test]
    public async Task ValidateWithSchema_InvalidEnumValue_IsReported()
    {
        (_, ConfigValidator validator, _) = Create();

        List<string> errors = validator.ValidateWithSchema("""
            vpn:
              type: not-a-protocol
            """);

        await Assert.That(errors.Any(e => e.Contains("type") && e.Contains("must be one of"))).IsTrue();
    }

    [Test]
    public async Task Build_ArrayOfObjects_CreatesItemPrototype()
    {
        ConfigSectionRegistry registry = new();
        ConfigDefaults.RegisterCoreSections(registry);

        ServiceCollection services = new();
        services.AddSingleton<IValidator<ArrkConfig>, ArrkConfigValidator>();
        ServiceProvider provider = services.BuildServiceProvider();

        ConfigSchemaGenerator generator = new(registry, provider);
        ConfigFormBuilder builder = new(generator, registry);

        ConfigFormModel model = builder.Build("""
            qualityProfiles:
              profiles:
                - name: My Profile
                  minResolution: 1080p
                  maxResolution: 2160p
            """);

        ConfigArrayNode profiles = FindNode(model, "qualityProfiles", "profiles") as ConfigArrayNode
            ?? throw new Exception("profiles not found");

        await Assert.That(profiles.Items.Count).IsEqualTo(1);
        await Assert.That(profiles.ItemPrototype).IsTypeOf<ConfigObjectNode>();

        string yaml = ConfigFormSerializer.Serialize(model);
        await Assert.That(yaml).Contains("My Profile");
    }

    [Test]
    public async Task Schema_EmitsNestedServicesShape()
    {
        (_, _, ConfigSectionRegistry registry) = Create();
        ServiceCollection services = new();
        ServiceProvider provider = services.BuildServiceProvider();
        ConfigSchemaGenerator generator = new(registry, provider);

        JsonElement schema = generator.GenerateSchema();
        JsonElement props = schema.GetProperty("properties");

        await Assert.That(props.TryGetProperty("services", out JsonElement servicesEl)).IsTrue();
        await Assert.That(servicesEl.GetProperty("properties").TryGetProperty("torrentDownloader", out _)).IsTrue();
    }

    [Test]
    public async Task FullRenderedDefaultConfig_ValidatesCleanly()
    {
        (_, ConfigValidator validator, ConfigSectionRegistry registry) = Create();

        Microsoft.Extensions.Configuration.IConfiguration empty =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        string yaml = ConfigRenderer.Render(empty, registry);

        List<string> schemaErrors = validator.ValidateWithSchema(yaml);
        ConfigValidationResult fluentErrors = validator.ValidateWithFluentValidation(yaml);

        await Assert.That(schemaErrors).IsEmpty();
        await Assert.That(fluentErrors.IsValid).IsTrue();
    }

    [Test]
    public async Task FullRenderedConfig_RoundTripsThroughFormUnchanged()
    {
        (ConfigFormBuilder builder, ConfigValidator validator, ConfigSectionRegistry registry) = Create();

        Microsoft.Extensions.Configuration.IConfiguration empty =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        string effective = ConfigRenderer.Render(empty, registry);

        ConfigFormModel model = builder.Build(effective);
        string regenerated = ConfigFormSerializer.Serialize(model);

        await Assert.That(validator.ValidateWithSchema(regenerated)).IsEmpty();
        await Assert.That(validator.ValidateWithFluentValidation(regenerated).IsValid).IsTrue();
    }

    [Test]
    public async Task Build_UntypedObject_PreservedAsRawNode()
    {
        ConfigSectionRegistry registry = new();
        registry.Register(new ConfigSectionProvider<AdvancedConfig>("advanced", "Advanced",
            icon: "Tune", group: "Test", order: 1, groupOrder: 50));

        ServiceCollection services = new();
        ServiceProvider provider = services.BuildServiceProvider();
        ConfigSchemaGenerator generator = new(registry, provider);
        ConfigFormBuilder builder = new(generator, registry);

        ConfigFormModel model = builder.Build("""
            advanced:
              name: hello
              settings:
                username: bob
                port: 8080
            """);

        ConfigFormNode? settings = FindNode(model, "advanced", "settings");
        await Assert.That(settings).IsTypeOf<ConfigRawNode>();

        string yaml = ConfigFormSerializer.Serialize(model);
        await Assert.That(yaml).Contains("username: bob");
        await Assert.That(yaml).Contains("port: 8080");
    }

    static ConfigScalarNode? FindScalar(ConfigFormModel model, string sectionPath, params string[] path)
        => FindNode(model, sectionPath, path) as ConfigScalarNode;

    static ConfigFormNode? FindNode(ConfigFormModel model, string sectionPath, params string[] path)
    {
        ConfigFormSection? section = model.FindSection(sectionPath);
        if (section == null)
            return null;

        ConfigFormNode current = section;
        foreach (string segment in path)
        {
            if (current is not ConfigObjectNode obj)
                return null;

            ConfigFormNode? next = obj.Children.FirstOrDefault(c => c.Key.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (next == null)
                return null;

            current = next;
        }

        return current;
    }

    [Test]
    public async Task Serialize_WithDefaults_OmitsUnchangedValues()
    {
        (ConfigFormBuilder builder, _, ConfigSectionRegistry registry) = Create();

        Microsoft.Extensions.Configuration.IConfiguration empty =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        string defaults = ConfigRenderer.Render(empty, registry);

        ConfigFormModel model = builder.Build(defaults);
        FindScalar(model, "vpn", "enabled")!.Value = true;
        FindScalar(model, "storage", "media")!.Value = "/mnt/media";

        string yaml = ConfigFormSerializer.Serialize(model, null, defaults);

        await Assert.That(yaml).Contains("enabled: true");
        await Assert.That(yaml).Contains("media: /mnt/media");
        // Untouched sections/fields stay out of the file.
        await Assert.That(yaml).DoesNotContain("downloads:");
        await Assert.That(yaml).DoesNotContain("notifications:");
        await Assert.That(yaml).DoesNotContain("monitoring:");
    }

    [Test]
    public async Task Serialize_WithDefaults_NoChanges_ProducesNoValues()
    {
        (ConfigFormBuilder builder, _, ConfigSectionRegistry registry) = Create();

        Microsoft.Extensions.Configuration.IConfiguration empty =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        string defaults = ConfigRenderer.Render(empty, registry);

        ConfigFormModel model = builder.Build(defaults);
        string yaml = ConfigFormSerializer.Serialize(model, null, defaults);

        await Assert.That(ConfigFormBuilder.ParseYaml(yaml).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Serialize_WithDefaults_ArrayChange_EmitsWholeArray()
    {
        (ConfigFormBuilder builder, _, ConfigSectionRegistry registry) = Create();

        Microsoft.Extensions.Configuration.IConfiguration empty =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        string defaults = ConfigRenderer.Render(empty, registry);

        ConfigFormModel model = builder.Build(defaults);
        ConfigArrayNode profiles = (ConfigArrayNode)FindNode(model, "qualityProfiles", "profiles")!;
        profiles.Items.Add(profiles.ItemPrototype!.Clone());

        string yaml = ConfigFormSerializer.Serialize(model, null, defaults);

        await Assert.That(yaml).Contains("profiles:");
        await Assert.That(ConfigFormBuilder.ParseYaml(yaml)["qualityProfiles"]).IsTypeOf<Dictionary<string, object?>>();
    }

    [Test]
    public async Task Serialize_WithDefaults_AndPreserve_KeepsUnknownKeys()
    {
        (ConfigFormBuilder builder, _, ConfigSectionRegistry registry) = Create();

        Microsoft.Extensions.Configuration.IConfiguration empty =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        string defaults = ConfigRenderer.Render(empty, registry);

        ConfigFormModel model = builder.Build(defaults);
        FindScalar(model, "vpn", "enabled")!.Value = true;

        string yaml = ConfigFormSerializer.Serialize(model, "customTopLevel:\n  foo: bar\n", defaults);

        await Assert.That(yaml).Contains("enabled: true");
        await Assert.That(yaml).Contains("customTopLevel");
        await Assert.That(yaml).Contains("foo: bar");
    }

    [Test]
    public async Task Build_MetadataSection_ComesFromRegistry()
    {
        (ConfigFormBuilder builder, _, ConfigSectionRegistry registry) = Create();

        ConfigFormModel model = builder.Build(ConfigRenderer.Render(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), registry));

        ConfigFormSection metadata = model.FindSection("metadata")!;
        await Assert.That(metadata).IsNotNull();
        await Assert.That(metadata.Title).IsEqualTo("Metadata");
        await Assert.That(FindScalar(model, "metadata", "tmdbApiKey")).IsNotNull();
    }

    public sealed class AdvancedConfig
    {
        public string Name { get; set; } = "";
        public Dictionary<string, object> Settings { get; set; } = new();
    }
}
