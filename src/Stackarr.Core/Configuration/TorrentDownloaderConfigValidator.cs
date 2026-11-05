using FluentValidation;

namespace Stackarr.Core;

public class TorrentDownloaderConfigValidator : AbstractValidator<TorrentDownloaderConfig>
{
    public TorrentDownloaderConfigValidator()
    {
        RuleFor(x => x.Seeding.MaxRatio).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Seeding.MaxTimeMinutes).GreaterThan(0);
        RuleFor(x => x.Seeding.MinTimeMinutes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Limits.MaxActiveDownloads).GreaterThan(0);
        RuleFor(x => x.Limits.MaxActiveSeeds).GreaterThan(0);
        RuleFor(x => x.Limits.MaxActiveTorrents).GreaterThan(0);
        RuleFor(x => x.Management.Slow.LimitMinutes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Management.Stalled.LimitMinutes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Management.ZeroProgressMultiplier).GreaterThanOrEqualTo(1);
        RuleFor(x => x.Management.RecoveryPerMinute).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Management.Storage.PauseDownloadsAtPercent).InclusiveBetween(0, 100);
        RuleFor(x => x.Management.Storage.CleanupSeedingAtPercent).InclusiveBetween(0, 100);
        RuleFor(x => x.Management.Storage.CleanupStrategy).Must(s => s is "oldest" or "newest" or "least_ratio")
            .WithMessage("Cleanup strategy must be 'oldest', 'newest', or 'least_ratio'");
    }
}
