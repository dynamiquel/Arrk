namespace Stackarr.Ui;

/// <summary>Standard tile sizes. The section grid chooses how many fit per row.</summary>
public enum TileSize
{
    Small,
    Medium,
    Large,
}

/// <summary>Where a tile is rendered. The dashboard is customisable; pages are not.</summary>
public enum TilePlacement
{
    Dashboard,
    Page,
}

/// <summary>
/// A widget a module contributes to the tile collection. <see cref="Component"/> renders the tile's
/// body and is handed a <see cref="TileContext"/>; the shell renders the shared frame (icon, title
/// and optional navigation) so every tile looks and behaves the same wherever it appears.
/// </summary>
/// <param name="Id">Stable identifier used by the dashboard YAML, e.g. <c>torrents.download</c>.</param>
/// <param name="Category">Grouping label, e.g. <c>Torrents</c>. Shown before the title on the dashboard.</param>
/// <param name="Title">The tile's own label, e.g. <c>Download</c>.</param>
/// <param name="Icon">MudBlazor icon constant.</param>
/// <param name="Component">The Razor component that renders the body.</param>
/// <param name="Order">Catalog order within a category.</param>
/// <param name="Description">Short explanation shown in the Tile Editor.</param>
/// <param name="Href">Optional page the tile navigates to when clicked.</param>
/// <param name="AdminOnly">Hidden entirely from non-admin users.</param>
/// <param name="LinkAdminOnly">The tile stays visible but only admins get the link.</param>
/// <param name="EnabledConfigPath">Optional config section path whose <c>enabled</c> flag gates the tile.</param>
/// <param name="DefaultSize">Initial size when the tile is added to a dashboard.</param>
/// <param name="DefaultSection">Section the tile is placed in by the default layout.</param>
/// <param name="DefaultOrder">Order within the default section (only used when <paramref name="DefaultSection"/> is set).</param>
/// <param name="Parameters">Optional parameters passed to the body component in addition to <see cref="TileContext"/>.</param>
public sealed record TileDefinition(
    string Id,
    string Category,
    string Title,
    string Icon,
    Type Component,
    int Order = 100,
    string? Description = null,
    string? Href = null,
    bool AdminOnly = false,
    bool LinkAdminOnly = false,
    string? EnabledConfigPath = null,
    TileSize DefaultSize = TileSize.Medium,
    string? DefaultSection = null,
    int DefaultOrder = 100,
    IReadOnlyDictionary<string, object?>? Parameters = null)
{
    /// <summary>
    /// The title shown by the shared tile frame. On the dashboard the category is prefixed
    /// (<c>TORRENTS - DOWNLOAD</c>) so a tile keeps its context when it is moved away from its
    /// owning page; on its own page the tile keeps its short title.
    /// </summary>
    public string TitleFor(TilePlacement placement) =>
        placement == TilePlacement.Dashboard
            ? $"{Category.ToUpperInvariant()} - {Title.ToUpperInvariant()}"
            : Title;
}

/// <summary>Everything a tile body needs to know about where it is being rendered.</summary>
public sealed record TileContext(
    TileDefinition Definition,
    TilePlacement Placement,
    bool IsAdmin,
    int UserId);

/// <summary>Contributes tiles to the shared collection. Modules register it in DI.</summary>
public interface ITileProvider
{
    IReadOnlyList<TileDefinition> Tiles { get; }
}
