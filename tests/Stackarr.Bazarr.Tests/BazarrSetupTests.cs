using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core;
using Stackarr.TestSupport;

namespace Stackarr.Bazarr.Tests;

public class BazarrSetupTests
{
    static StackarrInstance Instance() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "abc" })
            .Build());

    [Test]
    public async Task BuildProfile_AlwaysIncludesEnglishFirstAndUsesLastItemAsCutoff()
    {
        BazarrLanguageProfile profile = BazarrSetupService.BuildProfile(BazarrSetupService.ProfileId, "Stackarr Default", ["en", "fr", "de"]);

        await Assert.That(profile.ProfileId).IsEqualTo(BazarrSetupService.ProfileId);
        await Assert.That(profile.Items.Select(item => item.Language)).IsEquivalentTo(["en", "fr", "de"]);
        await Assert.That(profile.Cutoff).IsEqualTo(3);
        await Assert.That(profile.Items[0].Hi).IsEqualTo("False");
        await Assert.That(profile.Items[0].Forced).IsEqualTo("False");
    }

    [Test]
    public async Task BuildProviders_UsesTheConfiguredBazarrProviders()
    {
        List<string> providers = BazarrSetupService.BuildProviders(new BazarrConfig
        {
            Providers = ["gestdown", "subf2m", "gestdown"]
        });

        await Assert.That(providers).IsEquivalentTo(["gestdown", "subf2m"]);
        await Assert.That(providers).DoesNotContain("opensubtitlescom");
    }

    [Test]
    public async Task BuildSettings_UsesStackarrContainerNamesAndRepeatedLists()
    {
        FakeHttpMessageHandler handler = new();
        BazarrClient client = new("http://bazarr:6767", _ => Task.FromResult<string?>("key"), handler);
        BazarrSetupService setup = new(client, Instance(), null!, null!);

        List<KeyValuePair<string, string>> fields =
            setup.BuildSettingsFields(["en", "fr"], ["gestdown", "subtitlecat"]);

        await Assert.That(Field(fields, "settings-sonarr-ip")).IsEqualTo("stackarr-abc-sonarr");
        await Assert.That(Field(fields, "settings-sonarr-base_url")).IsEqualTo("/sonarr");
        await Assert.That(Field(fields, "settings-radarr-ip")).IsEqualTo("stackarr-abc-radarr");
        await Assert.That(Field(fields, "settings-general-serie_default_profile")).IsEqualTo("1");
        await Assert.That(fields.Count(field => field.Key == "languages-enabled")).IsEqualTo(2);
        await Assert.That(fields.Count(field => field.Key == "settings-general-enabled_providers")).IsEqualTo(2);

        string profileJson = Field(fields, "languages-profiles");
        using JsonDocument profile = JsonDocument.Parse(profileJson);
        JsonElement item = profile.RootElement[0];
        await Assert.That(item.GetProperty("profileId").GetInt32()).IsEqualTo(1);
        await Assert.That(item.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That(item.GetProperty("items")[0].GetProperty("language").GetString()).IsEqualTo("en");
    }

    [Test]
    public async Task BuildSettings_ClearsLegacyOpenSubtitlesCredentials()
    {
        FakeHttpMessageHandler handler = new();
        BazarrClient client = new("http://bazarr:6767", _ => Task.FromResult<string?>("key"), handler);
        BazarrSetupService setup = new(client, Instance(), null!, null!);

        List<KeyValuePair<string, string>> fields =
            setup.BuildSettingsFields(["en"], ["gestdown"]);

        await Assert.That(Field(fields, "settings-opensubtitlescom-username")).IsEqualTo("");
        await Assert.That(Field(fields, "settings-opensubtitlescom-password")).IsEqualTo("");
    }

    [Test]
    public async Task BuildSettings_KeepsBazarrSearchOutOfTheWay()
    {
        FakeHttpMessageHandler handler = new();
        BazarrClient client = new("http://bazarr:6767", _ => Task.FromResult<string?>("key"), handler);
        BazarrSetupService setup = new(client, Instance(), null!, null!);

        List<KeyValuePair<string, string>> fields =
            setup.BuildSettingsFields(["en"], ["gestdown"]);

        await Assert.That(Field(fields, "settings-general-wanted_search_frequency")).IsEqualTo("168");
        await Assert.That(Field(fields, "settings-sonarr-defer_search_signalr")).IsEqualTo("true");
        await Assert.That(Field(fields, "settings-radarr-defer_search_signalr")).IsEqualTo("true");
    }

    static string Field(List<KeyValuePair<string, string>> fields, string key) =>
        fields.First(field => field.Key == key).Value;
}
