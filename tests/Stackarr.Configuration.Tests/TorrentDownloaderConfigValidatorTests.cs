using FluentValidation.Results;
using Stackarr.Core;

namespace Stackarr.Configuration.Tests;

public class TorrentDownloaderConfigValidatorTests
{
    readonly TorrentDownloaderConfigValidator _validator = new();

    [Test]
    public async Task Validate_DefaultConfig_IsValid()
    {
        TorrentDownloaderConfig config = new();
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_NegativeMaxRatio_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Seeding = { MaxRatio = -1 }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_ZeroMaxRatio_IsValid()
    {
        TorrentDownloaderConfig config = new()
        {
            Seeding = { MaxRatio = 0 }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_ZeroMaxTimeMinutes_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Seeding = { MaxTimeMinutes = 0 }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_NegativeMinTimeMinutes_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Seeding = { MinTimeMinutes = -1 }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_ZeroMaxActiveDownloads_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Limits = { MaxActiveDownloads = 0 }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_ZeroMaxActiveSeeds_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Limits = { MaxActiveSeeds = 0 }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_ZeroMaxActiveTorrents_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Limits = { MaxActiveTorrents = 0 }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_NegativeStalledLimitMinutes_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Stalled = { LimitMinutes = -1 } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_ZeroProgressMultiplierBelowOne_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { ZeroProgressMultiplier = 0.5 }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_NegativeRecoveryPerMinute_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { RecoveryPerMinute = -0.5 }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_ZeroLimitStalledConfig_IsValid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Stalled = { Enabled = false, LimitMinutes = 0 } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_NegativeSlowLimitMinutes_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Slow = { LimitMinutes = -1 } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_PauseDownloadsAtPercent_BelowZero_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Storage = { PauseDownloadsAtPercent = -1 } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_PauseDownloadsAtPercent_Above100_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Storage = { PauseDownloadsAtPercent = 101 } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_CleanupSeedingAtPercent_0_IsValid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Storage = { CleanupSeedingAtPercent = 0 } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_CleanupSeedingAtPercent_100_IsValid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Storage = { CleanupSeedingAtPercent = 100 } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_InvalidCleanupStrategy_IsInvalid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Storage = { CleanupStrategy = "invalid" } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Validate_OldestCleanupStrategy_IsValid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Storage = { CleanupStrategy = "oldest" } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_NewestCleanupStrategy_IsValid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Storage = { CleanupStrategy = "newest" } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Validate_LeastRatioCleanupStrategy_IsValid()
    {
        TorrentDownloaderConfig config = new()
        {
            Management = { Storage = { CleanupStrategy = "least_ratio" } }
        };
        ValidationResult? result = await _validator.ValidateAsync(config);
        await Assert.That(result.IsValid).IsTrue();
    }
}
