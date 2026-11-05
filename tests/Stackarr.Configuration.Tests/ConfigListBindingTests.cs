using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core.Configuration;

namespace Stackarr.Configuration.Tests;

/// <summary>
/// Verifies that rendering/binding the config replaces list values instead of appending them to the
/// POCO defaults. <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/> appends to
/// existing collections, so binding into a default-initialised POCO ballooned
/// <c>qualityProfiles.profiles</c> (and each profile's <c>allowedQualities</c>, and
/// <c>services.jellyfin.libraries</c>) by the default count on every form Apply.
/// </summary>
public class ConfigListBindingTests
{
    static ConfigSectionRegistry Registry()
    {
        ConfigSectionRegistry registry = new();
        ConfigDefaults.RegisterCoreSections(registry);
        return registry;
    }

    static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    static IList ListAt(object root, params string[] path)
    {
        object? current = root;
        foreach (string segment in path)
        {
            current = current is Dictionary<string, object?> dict
                ? dict.First(e => string.Equals(e.Key, segment, StringComparison.OrdinalIgnoreCase)).Value
                : throw new InvalidOperationException($"{segment} is not a map");
        }

        return current as IList ?? throw new InvalidOperationException($"{string.Join('.', path)} is not a list");
    }

    [Test]
    public async Task RenderedConfig_DoesNotAppendDefaultsToConfiguredProfiles()
    {
        IConfiguration configuration = Config(
            ("qualityProfiles:profiles:0:name", "Custom"),
            ("qualityProfiles:profiles:0:allowedQualities:0", "Dvd"));

        string yaml = ConfigRenderer.Render(configuration, Registry());
        Dictionary<string, object?> graph = ConfigFormBuilder.ParseYaml(yaml);

        IList profiles = ListAt(graph, "qualityProfiles", "profiles");
        await Assert.That(profiles.Count).IsEqualTo(1);

        IList qualities = ListAt(profiles[0]!, "allowedQualities");
        await Assert.That(qualities.Count).IsEqualTo(1);
    }

    [Test]
    public async Task OptionsBinding_AlsoReplacesConfiguredProfiles()
    {
        IConfiguration configuration = Config(
            ("qualityProfiles:profiles:0:name", "Custom"),
            ("qualityProfiles:profiles:0:allowedQualities:0", "Dvd"));

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.ConfigureConfig<QualityProfilesConfig>(configuration.GetSection("qualityProfiles"));
        using ServiceProvider provider = services.BuildServiceProvider();

        QualityProfilesConfig bound = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<QualityProfilesConfig>>().Value;

        await Assert.That(bound.Profiles.Count).IsEqualTo(1);
        await Assert.That(bound.Profiles[0].AllowedQualities.Count).IsEqualTo(1);
    }

    [Test]
    public async Task OptionsBinding_BindsResolutionKeyedMinimumBitrates()
    {
        IConfiguration configuration = Config(
            ("qualityProfiles:minimumBitratesMbps:2160p", "15"),
            ("qualityProfiles:minimumBitratesMbps:1080p", "4"));

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.ConfigureConfig<QualityProfilesConfig>(configuration.GetSection("qualityProfiles"));
        using ServiceProvider provider = services.BuildServiceProvider();

        QualityProfilesConfig bound = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<QualityProfilesConfig>>().Value;

        await Assert.That(bound.MinimumBitratesMbps["2160p"]).IsEqualTo(15d);
        await Assert.That(bound.MinimumBitratesMbps["1080p"]).IsEqualTo(4d);
    }

    [Test]
    public async Task FormRoundTrip_IsStable_AndDoesNotGrowProfiles()
    {
        ConfigSectionRegistry registry = Registry();
        ConfigFormBuilder builder = new(
            new ConfigSchemaGenerator(registry, new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider()),
            registry);

        IConfiguration configuration = Config(
            ("qualityProfiles:profiles:0:name", "Custom"),
            ("qualityProfiles:profiles:0:allowedQualities:0", "Dvd"));

        string first = ConfigRenderer.Render(configuration, registry);
        ConfigFormModel model = builder.Build(first);
        string serialized = ConfigFormSerializer.Serialize(model, first);
        ConfigFormModel secondModel = builder.Build(serialized);
        string again = ConfigFormSerializer.Serialize(secondModel, serialized);

        await Assert.That(again).IsEqualTo(serialized);
    }
}
