
namespace Stackarr.Core.Lifecycle;

/// <summary>How close a media item is to its stale threshold.</summary>
public enum StaleLevel
{
    Fresh = 0,
    Expiring = 1,
    Stale = 2
}

/// <summary>
/// One library item a module can clean up. Providers opt in by implementing
/// <see cref="IManagedMediaProvider"/>; the lifecycle module never references TV or music types.
/// </summary>
public sealed record ManagedMediaItem(
    string ProviderKey,
    string ItemKey,
    MediaDomain Domain,
    string ItemType,
    string Title,
    string? Subtitle,
    string? ImageUrl,
    long SizeOnDisk,
    DateTime AddedAt,
    bool IsBookmarked = false,
    string? BookmarkLabel = null,
    string? LibraryId = null,
    string? ParentItemKey = null);

/// <summary>Outcome of deleting one managed item.</summary>
public sealed record ManagedMediaDeleteResult(bool Success, long BytesFreed, string? Message = null);

/// <summary>
/// Contributed by a library module (TV, music, or a plugin) so the lifecycle module can see what it
/// owns, how big it is, and how to delete it. Implementations are stateless and scoped to a service.
/// </summary>
public interface IManagedMediaProvider
{
    /// <summary>Media service key, matching the config section (<c>tv</c>, <c>music</c>, ...).</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>Only items that currently occupy disk space are returned.</summary>
    Task<IReadOnlyList<ManagedMediaItem>> GetItemsAsync(CancellationToken ct = default);

    Task<ManagedMediaDeleteResult> DeleteAsync(ManagedMediaItem item, CancellationToken ct = default);
}

/// <summary>Resolved stale thresholds for one service.</summary>
public sealed record StaleThresholds(
    double UpperThresholdDays,
    double LowerThresholdDays,
    bool AutoCleanup,
    double KeepThresholdDays,
    double KeepLowerThresholdDays)
{
    public static StaleThresholds Default { get; } = new(30, 0, false, 45, 0);

    public double ResolveUpper(bool isBookmarked) =>
        isBookmarked && KeepThresholdDays > 0 ? KeepThresholdDays : UpperThresholdDays;

    public double ResolveLower(bool isBookmarked) =>
        isBookmarked ? KeepLowerThresholdDays : LowerThresholdDays;
}

/// <summary>One evaluated item, with everything the UI and notifications need.</summary>
public sealed record StaleMediaItem(
    ManagedMediaItem Item,
    StaleLevel Level,
    DateTime BaselineAt,
    double AgeDays,
    double ThresholdDays,
    double? DaysRemaining,
    DateTime? LastConsumedAt,
    bool IsBookmarked,
    string? BookmarkLabel,
    bool IsPinned = false)
{
    public string ProviderKey => Item.ProviderKey;

    public string ItemKey => Item.ItemKey;

    /// <summary>Container identity (a season's series, an album's artist) when the provider supplies one.</summary>
    public string? ParentItemKey => Item.ParentItemKey;

    public bool IsStale => Level == StaleLevel.Stale;

    public bool IsExpiring => Level == StaleLevel.Expiring;

    public long ReclaimableBytes => IsStale ? Item.SizeOnDisk : 0;
}

/// <summary>
/// Lightweight staleness projection for a UI card or dialog: enough to render a badge and a pin
/// marker without carrying the whole managed item.
/// </summary>
public sealed record MediaStaleState(
    StaleLevel Level,
    double? DaysRemaining,
    bool IsBookmarked,
    string? BookmarkLabel,
    bool IsPinned)
{
    public static MediaStaleState Fresh { get; } = new(StaleLevel.Fresh, null, false, null, false);

    public static MediaStaleState From(StaleMediaItem item) =>
        new(item.Level, item.DaysRemaining, item.IsBookmarked, item.BookmarkLabel, item.IsPinned);
}

/// <summary>A full snapshot of every managed item for staleness evaluation.</summary>
public sealed record StaleMediaReport(
    IReadOnlyList<StaleMediaItem> Items,
    IReadOnlyDictionary<string, StaleThresholds> Thresholds,
    DateTime GeneratedAt)
{
    public IEnumerable<StaleMediaItem> Stale => Items.Where(x => x.IsStale);

    public IEnumerable<StaleMediaItem> Expiring => Items.Where(x => x.IsExpiring);

    public IEnumerable<StaleMediaItem> Pinned => Items.Where(x => x.IsPinned);

    public long StaleBytes => Stale.Sum(x => x.Item.SizeOnDisk);

    public long ExpiringBytes => Expiring.Sum(x => x.Item.SizeOnDisk);

    public IReadOnlyList<StaleMediaItem> ForProvider(string providerKey) =>
        Items.Where(x => x.ProviderKey == providerKey).ToList();
}

/// <summary>Implemented by the lifecycle module; consumed through <see cref="StaleMediaService"/>.</summary>
public interface IStaleMediaProvider
{
    string Name { get; }

    bool IsAvailable { get; }

    Task<StaleMediaReport> GetReportAsync(bool forceRefresh = false, CancellationToken ct = default);

    Task<ManagedMediaDeleteResult> DeleteAsync(string providerKey, string itemKey, CancellationToken ct = default);
}

/// <summary>
/// Facade over the contributed lifecycle provider. Always resolvable: with no lifecycle module the
/// UI simply reports that staleness tracking is unavailable.
/// </summary>
public sealed class StaleMediaService(IEnumerable<IStaleMediaProvider> providers)
{
    public IStaleMediaProvider? Active => providers.FirstOrDefault(x => x.IsAvailable);

    public bool IsAvailable => Active is not null;

    public Task<StaleMediaReport?> GetReportAsync(bool forceRefresh = false, CancellationToken ct = default) =>
        Active is { } provider
            ? Wrap(provider.GetReportAsync(forceRefresh, ct))
            : Task.FromResult<StaleMediaReport?>(null);

    public async Task<IReadOnlyDictionary<string, StaleMediaItem>> GetStatusesAsync(
        string providerKey,
        CancellationToken ct = default)
    {
        StaleMediaReport? report = await GetReportAsync(false, ct);
        if (report is null)
            return new Dictionary<string, StaleMediaItem>(StringComparer.Ordinal);
        return report.Items
            .Where(x => x.ProviderKey == providerKey)
            .ToDictionary(x => x.ItemKey, x => x, StringComparer.Ordinal);
    }

    public Task<ManagedMediaDeleteResult> DeleteAsync(
        string providerKey,
        string itemKey,
        CancellationToken ct = default) =>
        Active is { } provider
            ? provider.DeleteAsync(providerKey, itemKey, ct)
            : Task.FromResult(new ManagedMediaDeleteResult(false, 0, "Staleness tracking is not available."));

    static async Task<StaleMediaReport?> Wrap(Task<StaleMediaReport> task) => await task;
}
