using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core;
using Stackarr.Core.Configuration;

namespace Stackarr.Configuration.Tests;

public class ConfigSchemaGeneratorTests
{
    readonly ConfigSectionRegistry _registry;
    readonly ConfigSchemaGenerator _generator;

    public ConfigSchemaGeneratorTests()
    {
        _registry = new();
        ConfigDefaults.RegisterCoreSections(_registry);

        ServiceCollection services = new ServiceCollection();
        services.AddSingleton<IValidator<StackarrConfig>, StackarrConfigValidator>();
        services.AddSingleton<IValidator<StorageConfig>, StorageConfigValidator>();
        services.AddSingleton<IValidator<VpnConfig>, VpnConfigValidator>();
        services.AddSingleton<IValidator<TorrentDownloaderConfig>, TorrentDownloaderConfigValidator>();
        ServiceProvider provider = services.BuildServiceProvider();

        _generator = new(_registry, provider);
    }

    [Test]
    public async Task GenerateSchema_ReturnsValidJsonObject()
    {
        JsonElement schema = _generator.GenerateSchema();

        await Assert.That(schema.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(schema.TryGetProperty("properties", out _)).IsTrue();
    }

    [Test]
    public async Task GenerateSchema_ContainsAllCoreSections()
    {
        JsonElement props = GetRootProperties();

        await Assert.That(props.TryGetProperty("storage", out _)).IsTrue();
        await Assert.That(props.TryGetProperty("vpn", out _)).IsTrue();
        await Assert.That(props.TryGetProperty("routes", out _)).IsTrue();
        await Assert.That(props.TryGetProperty("notifications", out _)).IsTrue();
        await Assert.That(props.TryGetProperty("monitoring", out _)).IsTrue();
        await Assert.That(props.TryGetProperty("qualityProfiles", out _)).IsTrue();
        await Assert.That(props.TryGetProperty("metadata", out _)).IsTrue();
    }

    [Test]
    public async Task GenerateSchema_StorageMedia_IsStringWithDescriptionAndMinLength()
    {
        JsonElement media = GetProperty("storage", "media");

        await Assert.That(media.GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(media.GetProperty("description").GetString()).IsNotEmpty();
        await Assert.That(media.GetProperty("minLength").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task GenerateSchema_VpnEnabled_IsBooleanType()
    {
        JsonElement enabled = GetProperty("vpn", "enabled");

        await Assert.That(enabled.GetProperty("type").GetString()).IsEqualTo("boolean");
    }

    [Test]
    public async Task GenerateSchema_NestedObject_HasProperties()
    {
        JsonElement routes = GetRootProperties().GetProperty("routes");

        await Assert.That(routes.GetProperty("properties").TryGetProperty("cloudflared", out _)).IsTrue();
    }

    [Test]
    public async Task GenerateSchema_Routes_HasPublicUrl_AndCloudflared_HasTunnelToken()
    {
        JsonElement routes = GetProperty("routes");
        await Assert.That(routes.GetProperty("properties").TryGetProperty("publicUrl", out _)).IsTrue();

        JsonElement cloudflared = GetProperty("routes", "cloudflared");
        JsonElement properties = cloudflared.GetProperty("properties");

        await Assert.That(properties.TryGetProperty("enabled", out _)).IsTrue();
        await Assert.That(properties.TryGetProperty("tunnelToken", out _)).IsTrue();
        await Assert.That(properties.TryGetProperty("publicUrl", out _)).IsFalse();
    }

    [Test]
    public async Task GenerateSchema_DictionaryProperty_IsObjectType()
    {
        JsonElement customRoutes = GetProperty("routes", "customRoutes");

        await Assert.That(customRoutes.GetProperty("type").GetString()).IsEqualTo("object");
    }

    [Test]
    public async Task GenerateSchema_QualitiesProfiles_IsArrayType()
    {
        JsonElement profiles = GetProperty("qualityProfiles", "profiles");

        await Assert.That(profiles.GetProperty("type").GetString()).IsEqualTo("array");
    }

    [Test]
    public async Task GenerateSchema_MinimumBitrates_IsObjectType()
    {
        JsonElement bitrates = GetProperty("qualityProfiles", "minimumBitratesMbps");

        await Assert.That(bitrates.GetProperty("type").GetString()).IsEqualTo("object");
    }

    [Test]
    public async Task GenerateSchema_RegisteredModuleSection_AppearsUnderServices()
    {
        _registry.Register(new ConfigSectionProvider<TorrentDownloaderConfig>("services:torrent_downloader", "Downloads"));

        JsonElement props = GetRootProperties();

        await Assert.That(props.TryGetProperty("services", out JsonElement services)).IsTrue();
        await Assert.That(services.GetProperty("properties").TryGetProperty("torrentDownloader", out _)).IsTrue();
    }

    [Test]
    public async Task GenerateSchema_NestedFluentValidationRule_IsNotSurfaced_CurrentLimitation()
    {
        // Rules declared against nested members, such as RuleFor(x => x.Seeding.MaxRatio), are not
        // surfaced on the child object. This documents the current behaviour of the generator.
        _registry.Register(new ConfigSectionProvider<TorrentDownloaderConfig>("services:torrent_downloader", "Downloads"));

        JsonElement maxRatio = GetProperty("services", "torrentDownloader", "seeding", "maxRatio");

        await Assert.That(maxRatio.GetProperty("type").GetString()).IsEqualTo("number");
        await Assert.That(maxRatio.TryGetProperty("minimum", out _)).IsFalse();
    }

    [Test]
    public async Task GenerateSchema_NotEmptyRule_SetsMinLengthOnString()
    {
        // storage.media / storage.downloads are unconditionally NotEmpty, so the schema
        // marks them required via minLength.
        JsonElement media = GetProperty("storage", "media");

        await Assert.That(media.TryGetProperty("minLength", out JsonElement min)).IsTrue();
        await Assert.That(min.GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task GenerateSchema_ConditionalNotEmptyRule_IsNotSurfaced()
    {
        // vpn.wireguardPrivateKey is only required When(Vpn.Enabled); the schema cannot
        // express that condition, so it must not mark the field as required.
        JsonElement privateKey = GetProperty("vpn", "wireguardPrivateKey");

        await Assert.That(privateKey.GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(privateKey.TryGetProperty("minLength", out _)).IsFalse();
    }

    [Test]
    public async Task GenerateSchema_UnknownSectionName_IsNotPresent()
    {
        JsonElement props = GetRootProperties();

        await Assert.That(props.TryGetProperty("does_not_exist", out _)).IsFalse();
    }

    JsonElement GetRootProperties()
    {
        JsonElement schema = _generator.GenerateSchema();
        return schema.GetProperty("properties");
    }

    JsonElement GetProperty(params string[] path)
    {
        JsonElement props = GetRootProperties();
        JsonElement current = default;
        foreach (string segment in path)
        {
            current = props.GetProperty(segment);
            if (current.TryGetProperty("properties", out JsonElement nested))
                props = nested;
        }
        return current;
    }
}

public class StalenessSchemaTests
{
    [Test]
    public async Task Schema_ExposesPerServiceStalenessKeys()
    {
        ConfigSectionRegistry registry = new();
        ConfigDefaults.RegisterCoreSections(registry);
        ServiceCollection services = new ServiceCollection();
        services.AddSingleton<IValidator<StackarrConfig>, StackarrConfigValidator>();
        ServiceProvider provider = services.BuildServiceProvider();
        ConfigSchemaGenerator generator = new(registry, provider);

        JsonElement schema = generator.GenerateSchema();
        JsonElement tv = schema.GetProperty("properties").GetProperty("monitoring")
            .GetProperty("properties").GetProperty("tv")
            .GetProperty("properties").GetProperty("stale").GetProperty("properties");

        await Assert.That(tv.TryGetProperty("upperThreshold", out _)).IsTrue();
        await Assert.That(tv.TryGetProperty("lowerThreshold", out _)).IsTrue();
        await Assert.That(tv.TryGetProperty("autoCleanup", out _)).IsTrue();
        await Assert.That(tv.TryGetProperty("keepThreshold", out _)).IsTrue();
    }
}
