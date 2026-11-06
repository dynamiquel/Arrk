using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Tvarr;

namespace Arrk.Orchestration.Tests;

/// <summary>
/// Covers the live-editor validation path (<see cref="ConfigValidator"/>):
/// YAML syntax checks, FluentValidation-based section validation, and schema validation.
/// </summary>
public class LiveConfigValidatorTests
{
    static ConfigValidator CreateValidator()
    {
        ConfigSectionRegistry registry = new();
        ConfigDefaults.RegisterCoreSections(registry);
        registry.Register(new ConfigSectionProvider<TvConfig>("services:tv", "TV"));

        ServiceCollection services = new();
        services.AddSingleton<IValidator<ArrkConfig>, ArrkConfigValidator>();
        services.AddSingleton<IValidator<StorageConfig>, StorageConfigValidator>();
        services.AddSingleton<IValidator<VpnConfig>, VpnConfigValidator>();
        ServiceProvider provider = services.BuildServiceProvider();

        ConfigSchemaGenerator generator = new(registry, provider);
        return new ConfigValidator(generator, registry, provider);
    }

    static ConfigValidator CreateValidatorWithTorrent()
    {
        ConfigSectionRegistry registry = new();
        ConfigDefaults.RegisterCoreSections(registry);
        registry.Register(new ConfigSectionProvider<TorrentDownloaderConfig>("services:torrentDownloader", "Downloads"));

        ServiceCollection services = new();
        services.AddSingleton<IValidator<ArrkConfig>, ArrkConfigValidator>();
        services.AddSingleton<IValidator<VpnConfig>, VpnConfigValidator>();
        services.AddSingleton<IValidator<TorrentDownloaderConfig>, TorrentDownloaderConfigValidator>();
        ServiceProvider provider = services.BuildServiceProvider();

        ConfigSchemaGenerator generator = new(registry, provider);
        return new ConfigValidator(generator, registry, provider);
    }

    [Test]
    public async Task ValidateVpnRouting_TorrentOptInWithoutCredentials_IsInvalid()
    {
        ConfigValidator validator = CreateValidatorWithTorrent();

        ConfigValidationResult result = validator.ValidateWithFluentValidation("""
            vpn:
              provider: protonvpn
              type: wireguard
            services:
              torrentDownloader:
                vpn: true
            """);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.Contains("vpn.wireguardPrivateKey"))).IsTrue();
    }

    [Test]
    public async Task ValidateVpnRouting_TorrentOptInWithCredentials_IsValid()
    {
        ConfigValidator validator = CreateValidatorWithTorrent();

        ConfigValidationResult result = validator.ValidateWithFluentValidation("""
            vpn:
              provider: protonvpn
              type: wireguard
              wireguardPrivateKey: key
              wireguardAddresses: 10.2.0.2/32
            services:
              torrentDownloader:
                vpn: true
            """);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task ValidateVpnRouting_TorrentNotOptedIn_AllowsEmptyVpn()
    {
        ConfigValidator validator = CreateValidatorWithTorrent();

        ConfigValidationResult result = validator.ValidateWithFluentValidation("""
            services:
              torrentDownloader:
                vpn: false
            """);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task CheckYamlSyntax_ValidOrEmpty_ReturnsNull()
    {
        ConfigValidator validator = CreateValidator();

        await Assert.That(validator.CheckYamlSyntax("storage:\n  media: /mnt")).IsNull();
        await Assert.That(validator.CheckYamlSyntax("")).IsNull();
        await Assert.That(validator.CheckYamlSyntax("   ")).IsNull();
    }

    [Test]
    public async Task CheckYamlSyntax_Invalid_ReturnsError()
    {
        ConfigValidator validator = CreateValidator();

        string? error = validator.CheckYamlSyntax("storage:\n  media: [unterminated");

        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("YAML syntax error");
    }

    [Test]
    public async Task ValidateWithFluentValidation_ValidYaml_IsValid()
    {
        ConfigValidator validator = CreateValidator();

        ConfigValidationResult result = validator.ValidateWithFluentValidation(
            "storage:\n  media: /mnt/media\n  downloads: /mnt/downloads");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Errors).IsEmpty();
    }

    [Test]
    public async Task ValidateWithFluentValidation_EmptyStorageMedia_ReportsError()
    {
        ConfigValidator validator = CreateValidator();

        ConfigValidationResult result = validator.ValidateWithFluentValidation(
            "storage:\n  media: \"\"\n  downloads: /mnt/downloads");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.Contains("Storage"))).IsTrue();
    }

    [Test]
    public async Task ValidateWithFluentValidation_EmptyContent_IsInvalid()
    {
        ConfigValidator validator = CreateValidator();

        ConfigValidationResult result = validator.ValidateWithFluentValidation("");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains("Configuration is empty");
    }

    [Test]
    public async Task ValidateWithFluentValidation_SyntaxError_IsInvalid()
    {
        ConfigValidator validator = CreateValidator();

        ConfigValidationResult result = validator.ValidateWithFluentValidation("storage: [oops");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.Contains("YAML syntax error"))).IsTrue();
    }

    [Test]
    public async Task ValidateWithSchema_RootUnknownProperty_ReportsError()
    {
        ConfigValidator validator = CreateValidator();

        List<string> errors = validator.ValidateWithSchema("definitelyNotASection:\n  foo: 1");

        await Assert.That(errors.Any(e => e.Contains("definitelyNotASection"))).IsTrue();
        await Assert.That(errors.Any(e => e.Contains("unknown property"))).IsTrue();
    }

    [Test]
    public async Task ValidateWithSchema_NestedUnknownProperty_IsDetected()
    {
        // Unknown keys nested inside a known section must be reported.
        ConfigValidator validator = CreateValidator();

        List<string> errors = validator.ValidateWithSchema("storage:\n  definitelyNotAProperty: 1");

        await Assert.That(errors.Any(e => e.Contains("definitelyNotAProperty"))).IsTrue();
        await Assert.That(errors.Any(e => e.Contains("unknown property"))).IsTrue();
    }

    [Test]
    public async Task ValidateWithSchema_ValidContent_HasNoErrors()
    {
        ConfigValidator validator = CreateValidator();

        List<string> errors = validator.ValidateWithSchema("storage:\n  media: /mnt/media\n  downloads: /mnt/downloads");

        await Assert.That(errors).IsEmpty();
    }
}
