using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core.Configuration;

namespace Arrk.Configuration.Tests;

public class ConfigAutocompleteProviderTests
{
    readonly ConfigAutocompleteProvider _provider;

    public ConfigAutocompleteProviderTests()
    {
        ConfigSectionRegistry registry = new ConfigSectionRegistry();
        ConfigDefaults.RegisterCoreSections(registry);

        ServiceCollection services = new ServiceCollection();
        services.AddSingleton<IValidator<ArrkConfig>, ArrkConfigValidator>();
        services.AddSingleton<IValidator<StorageConfig>, StorageConfigValidator>();
        services.AddSingleton<IValidator<VpnConfig>, VpnConfigValidator>();
        ServiceProvider provider = services.BuildServiceProvider();

        ConfigSchemaGenerator generator = new(registry, provider);
        _provider = new(generator);
    }

    static List<string> Labels(AutocompleteResult result) =>
        result.Suggestions.Select(s => s.Label).ToList();

    [Test]
    public async Task GetSuggestions_EmptyText_ReturnsTopLevelSections()
    {
        AutocompleteResult result = _provider.GetSuggestions("", 0);
        List<string> labels = Labels(result);

        await Assert.That(labels).IsNotEmpty();
        await Assert.That(labels).Contains("storage");
        await Assert.That(labels).Contains("vpn");
        await Assert.That(labels).Contains("routes");
        await Assert.That(labels).Contains("monitoring");
        await Assert.That(labels).Contains("qualityProfiles");
    }

    [Test]
    public async Task GetSuggestions_AfterStorageColon_SuggestsStorageProperties()
    {
        string yaml = "storage:\n  ";
        AutocompleteResult result = _provider.GetSuggestions(yaml, yaml.Length);
        List<string> labels = Labels(result);

        await Assert.That(labels).Contains("media");
        await Assert.That(labels).Contains("downloads");
        await Assert.That(labels).Contains("tv");
        await Assert.That(labels).Contains("music");
    }

    [Test]
    public async Task GetSuggestions_AfterBooleanColon_SuggestsBooleanValues()
    {
        string yaml = "vpn:\n  enabled:";
        AutocompleteResult result = _provider.GetSuggestions(yaml, yaml.Length);
        List<string> labels = Labels(result);

        await Assert.That(labels).Contains("true");
        await Assert.That(labels).Contains("false");
    }

    [Test]
    public async Task GetSuggestions_BooleanValue_HasBooleanTypeHint()
    {
        string yaml = "vpn:\n  enabled:";
        AutocompleteResult result = _provider.GetSuggestions(yaml, yaml.Length);
        AutocompleteSuggestion? suggestion = result.Suggestions.FirstOrDefault(s => s.Label == "true");

        await Assert.That(suggestion).IsNotNull();
        await Assert.That(suggestion!.TypeHint).IsEqualTo("boolean");
    }

    [Test]
    public async Task GetSuggestions_StorageProperty_HasDescription()
    {
        string yaml = "storage:\n  ";
        AutocompleteResult result = _provider.GetSuggestions(yaml, yaml.Length);
        AutocompleteSuggestion? mediaSuggestion = result.Suggestions.FirstOrDefault(s => s.Label == "media");

        await Assert.That(mediaSuggestion).IsNotNull();
        await Assert.That(mediaSuggestion!.Description).IsNotEmpty();
    }

    [Test]
    public async Task GetSuggestions_Context_ReportsCurrentKeyAndParents()
    {
        string yaml = "storage:\n  media:";
        AutocompleteResult result = _provider.GetSuggestions(yaml, yaml.Length);

        await Assert.That(result.Context.ParentKeys).Contains("storage");
        await Assert.That(result.Context.CurrentKey).IsEqualTo("media");
    }

    [Test]
    public async Task GetSuggestions_NestedSection_SuggestsNestedProperties()
    {
        string yaml = "monitoring:\n  speedtest:\n    ";
        AutocompleteResult result = _provider.GetSuggestions(yaml, yaml.Length);
        List<string> labels = Labels(result);

        await Assert.That(labels).Contains("enabled");
        await Assert.That(labels).Contains("schedule");
    }

    [Test]
    public async Task GetSuggestions_StringProperty_SuggestsQuotedStringPlaceholder()
    {
        string yaml = "storage:\n  media:";
        AutocompleteResult result = _provider.GetSuggestions(yaml, yaml.Length);
        AutocompleteSuggestion? stringSuggestion = result.Suggestions.FirstOrDefault(s => s.TypeHint == "string");

        await Assert.That(stringSuggestion).IsNotNull();
        await Assert.That(stringSuggestion!.Label).IsEqualTo("\"\"");
    }

    [Test]
    public async Task GetSuggestions_AllSuggestions_HaveLabelAndInsertText()
    {
        AutocompleteResult result = _provider.GetSuggestions("", 0);

        await Assert.That(result.Suggestions).IsNotEmpty();
        foreach (AutocompleteSuggestion suggestion in result.Suggestions)
        {
            await Assert.That(suggestion.Label).IsNotEmpty();
            await Assert.That(suggestion.InsertText).IsNotEmpty();
        }
    }
}
