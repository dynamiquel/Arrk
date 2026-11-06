using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Enums;
using Arrk.Core.Lifecycle;

namespace Arrk.MediaLifecycle;

/// <summary>
/// Last staleness level we announced for an item. Persisted so a job restart (or hourly poll) never
/// re-sends the same "about to be stale" / "stale" alert.
/// </summary>
public class LifecycleItemState
{
    public long Id { get; set; }

    public string ProviderKey { get; set; } = "";

    public string ItemKey { get; set; } = "";

    public string ItemType { get; set; } = "";

    public string Title { get; set; } = "";

    public StaleLevel Level { get; set; }

    public DateTime? FirstSeenAt { get; set; }

    public DateTime? LastNotifiedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed class LifecycleItemStateConfiguration : ModuleEntityConfiguration<LifecycleItemState>
{
    public override void Configure(EntityTypeBuilder<LifecycleItemState> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ItemKey).HasMaxLength(512).IsRequired();
        builder.Property(x => x.ItemType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(512).IsRequired();
        builder.HasIndex(x => new { x.ProviderKey, x.ItemKey }).IsUnique();
        builder.HasIndex(x => x.Level);
    }
}

/// <summary>
/// Staleness evaluator: combines managed library items with durable consumption stamps and the
/// per-service config thresholds. Results are cached briefly because both the admin page and the
/// TV/music pages ask for them.
/// </summary>
public sealed class MediaLifecycleService(
    IEnumerable<IManagedMediaProvider> managedProviders,
    Arrk.Core.Playback.WatchActivityService watch,
    MediaPinService pins,
    Microsoft.Extensions.Options.IOptionsMonitor<Arrk.Core.Configuration.ArrkConfig> config,
    TimeProvider time,
    ILogger<MediaLifecycleService> logger) : IStaleMediaProvider
{
    static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(20);

    readonly IReadOnlyList<IManagedMediaProvider> _providers = managedProviders.ToList();
    readonly SemaphoreSlim _gate = new(1, 1);
    StaleMediaReport? _cached;
    DateTime _cachedAt = DateTime.MinValue;

    public string Name => "Media lifecycle";

    public bool IsAvailable => true;

    public IReadOnlyList<IManagedMediaProvider> Providers => _providers;

    public async Task<StaleMediaReport> GetReportAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        if (!forceRefresh && _cached is not null && now - _cachedAt < CacheLifetime)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            now = time.GetUtcNow().UtcDateTime;
            if (!forceRefresh && _cached is not null && now - _cachedAt < CacheLifetime)
                return _cached;

            return _cached = await BuildAsync(now, ct);
        }
        finally
        {
            _cachedAt = time.GetUtcNow().UtcDateTime;
            _gate.Release();
        }
    }

    public async Task<ManagedMediaDeleteResult> DeleteAsync(
        string providerKey,
        string itemKey,
        CancellationToken ct = default)
    {
        StaleMediaReport report = await GetReportAsync(false, ct);
        StaleMediaItem? target = report.Items.FirstOrDefault(x =>
            x.ProviderKey == providerKey && x.ItemKey == itemKey);
        if (target is null)
            return new ManagedMediaDeleteResult(false, 0, "That item is no longer in the library.");
        if (target.IsPinned)
            return new ManagedMediaDeleteResult(false, 0, "This item is pinned and cannot be deleted.");

        IManagedMediaProvider? provider = _providers.FirstOrDefault(x => x.Key == providerKey);
        if (provider is null)
            return new ManagedMediaDeleteResult(false, 0, "No library provider is registered for that service.");

        ManagedMediaDeleteResult result = await provider.DeleteAsync(target.Item, ct);
        if (result.Success)
        {
            _cached = null;
            logger.LogInformation(
                "Deleted stale {Service} item {Title} ({Bytes} bytes freed)",
                providerKey, target.Item.Title, result.BytesFreed);
        }
        return result;
    }

    public StaleThresholds GetThresholds(string providerKey)
    {
        Arrk.Core.Configuration.StalenessConfig stale = providerKey switch
        {
            Arrk.Core.Playback.WatchActivityServices.Tv => config.CurrentValue.Monitoring.Tv.Stale,
            Arrk.Core.Playback.WatchActivityServices.Music => config.CurrentValue.Monitoring.Music.Stale,
            _ => new Arrk.Core.Configuration.StalenessConfig()
        };

        return new StaleThresholds(
            stale.UpperThreshold,
            stale.LowerThreshold,
            stale.AutoCleanup,
            stale.KeepThreshold,
            stale.KeepLowerThreshold);
    }

    async Task<StaleMediaReport> BuildAsync(DateTime now, CancellationToken ct)
    {
        Dictionary<string, StaleThresholds> thresholds = new(StringComparer.Ordinal);
        List<StaleMediaItem> evaluated = [];

        foreach (IManagedMediaProvider provider in _providers)
        {
            thresholds[provider.Key] = GetThresholds(provider.Key);
            IReadOnlyList<ManagedMediaItem> items;
            try
            {
                items = await provider.GetItemsAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read managed media for {Provider}", provider.Key);
                continue;
            }

            IReadOnlyDictionary<string, Arrk.Core.Playback.MediaConsumptionStamp> stamps =
                await watch.GetConsumptionAsync(provider.Key, ct);
            IReadOnlySet<string> pinned = await pins.GetPinnedKeysAsync(provider.Key, ct);
            StaleThresholds limits = thresholds[provider.Key];

            foreach (ManagedMediaItem item in items)
            {
                bool isPinned = pinned.Contains(item.ItemKey)
                    || (item.ParentItemKey is { Length: > 0 } parent && pinned.Contains(parent));

                stamps.TryGetValue(item.ItemKey, out Arrk.Core.Playback.MediaConsumptionStamp? stamp);
                DateTime baseline = item.AddedAt;
                if (stamp is not null && stamp.LastConsumedAt > baseline)
                    baseline = stamp.LastConsumedAt;
                if (baseline == default)
                    baseline = now;

                double age = Math.Max(0, (now - baseline.ToUniversalTime()).TotalDays);
                double upper = limits.ResolveUpper(item.IsBookmarked);
                double lower = limits.ResolveLower(item.IsBookmarked);

                StaleLevel level = isPinned || upper <= 0
                    ? StaleLevel.Fresh
                    : age >= upper
                        ? StaleLevel.Stale
                        : lower > 0 && age >= lower
                            ? StaleLevel.Expiring
                            : StaleLevel.Fresh;

                double? remaining = isPinned || upper <= 0 ? null : upper - age;

                evaluated.Add(new StaleMediaItem(
                    item,
                    level,
                    baseline,
                    age,
                    upper <= 0 ? 0 : upper,
                    remaining,
                    stamp?.LastConsumedAt,
                    item.IsBookmarked,
                    item.BookmarkLabel,
                    isPinned));
            }
        }

        return new StaleMediaReport(evaluated, thresholds, now);
    }
}
