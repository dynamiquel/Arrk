using FluentValidation.Results;
using Stackarr.Core.Configuration;

namespace Stackarr.Configuration.Tests;

public class StorageConfigValidatorTests
{
    readonly StorageConfigValidator _validator = new();

    [Test]
    public async Task Validate_DefaultConfig_IsValid()
    {
        StorageConfig config = new();

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_EmptyMedia_IsInvalid()
    {
        StorageConfig config = new() { Media = "" };

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Media");
    }

    [Test]
    public async Task Validate_EmptyDownloads_IsInvalid()
    {
        StorageConfig config = new() { Downloads = "" };

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).Contains(e => e.PropertyName == "Downloads");
    }

    [Test]
    public async Task Validate_EmptyRelativePaths_AreStillValid()
    {
        // Tv/Music/Config are not required to be non-empty by this validator.
        StorageConfig config = new() { Tv = "", Music = "" };

        ValidationResult result = await _validator.ValidateAsync(config);

        await Assert.That(result.IsValid).IsTrue();
    }
}
