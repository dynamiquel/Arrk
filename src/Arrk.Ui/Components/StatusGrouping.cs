using MudBlazor;

namespace Arrk.Ui.Components;

/// <summary>Primary status buckets used to group library/download surfaces into collapsible
/// sections. Each item belongs to exactly one bucket (chosen by priority), so nothing is rendered
/// twice when the sections are stacked.</summary>
public enum StatusGroupKind
{
    Downloaded,
    Downloading,
    Missing,
    Seeding,
    Paused,
    Queued,
    Error,
    Other
}

/// <summary>Static presentation for one <see cref="StatusGroupKind"/> section.</summary>
public sealed record StatusSection(StatusGroupKind Kind, string Title, string Icon, string Accent);

/// <summary>An ordered, non-empty group of items sharing one primary status.</summary>
public sealed record StatusGroup<TItem>(StatusSection Section, IReadOnlyList<TItem> Items)
{
    public StatusGroupKind Kind => Section.Kind;
    public string Title => Section.Title;
    public string Icon => Section.Icon;
    public string Accent => Section.Accent;
    public int Count => Items.Count;
}

/// <summary>
/// The shared vocabulary and bucketing algorithm behind every status-grouped surface (the Movies,
/// Shows, Music and Downloads pages). Modules supply only their own classifier; the sections, their
/// canonical order and the "skip empty groups" rule live here so every page reads the same.
/// </summary>
public static class StatusGrouping
{
    public static readonly StatusSection DownloadedSection = new(StatusGroupKind.Downloaded, "Downloaded", Icons.Material.Filled.CheckCircle, "green");
    public static readonly StatusSection DownloadingSection = new(StatusGroupKind.Downloading, "Downloading", Icons.Material.Filled.CloudDownload, "blue");
    public static readonly StatusSection MissingSection = new(StatusGroupKind.Missing, "Missing", Icons.Material.Filled.ErrorOutline, "red");
    public static readonly StatusSection SeedingSection = new(StatusGroupKind.Seeding, "Seeding", Icons.Material.Filled.ArrowUpward, "green");
    public static readonly StatusSection PausedSection = new(StatusGroupKind.Paused, "Paused", Icons.Material.Filled.Pause, "");
    public static readonly StatusSection QueuedSection = new(StatusGroupKind.Queued, "Queued", Icons.Material.Filled.Schedule, "");
    public static readonly StatusSection ErrorSection = new(StatusGroupKind.Error, "Error", Icons.Material.Filled.ErrorOutline, "red");
    public static readonly StatusSection OtherSection = new(StatusGroupKind.Other, "Other", Icons.Material.Filled.HelpOutline, "");

    // Downloaded first: what you already have is the most relevant thing to see.
    public static readonly StatusSection[] LibrarySections = [DownloadedSection, DownloadingSection, MissingSection];
    // Music: an active download is the most transient thing to surface, then what you hold, then
    // what is genuinely missing.
    public static readonly StatusSection[] MusicSections = [DownloadingSection, DownloadedSection, MissingSection];
    public static readonly StatusSection[] TorrentSections = [DownloadingSection, QueuedSection, SeedingSection, PausedSection, ErrorSection, OtherSection];
    // Unified downloads: active first, then queued, seeding, completed (torrents seeding /
    // Soulseek done), paused, errors.
    public static readonly StatusSection[] DownloadSections = [DownloadingSection, QueuedSection, SeedingSection, DownloadedSection, PausedSection, ErrorSection];

    /// <summary>Buckets the items, then emits the sections in canonical order, skipping empty ones.</summary>
    public static IReadOnlyList<StatusGroup<TItem>> Build<TItem>(
        IEnumerable<TItem> items,
        Func<TItem, StatusGroupKind> classify,
        IReadOnlyList<StatusSection> sections)
    {
        Dictionary<StatusGroupKind, List<TItem>> buckets = [];
        foreach (TItem item in items)
        {
            StatusGroupKind kind = classify(item);
            if (!buckets.TryGetValue(kind, out List<TItem>? bucket))
            {
                bucket = [];
                buckets[kind] = bucket;
            }
            bucket.Add(item);
        }

        List<StatusGroup<TItem>> groups = [];
        foreach (StatusSection section in sections)
        {
            if (buckets.TryGetValue(section.Kind, out List<TItem>? bucket) && bucket.Count > 0)
            {
                groups.Add(new StatusGroup<TItem>(section, bucket));
            }
        }

        return groups;
    }
}
