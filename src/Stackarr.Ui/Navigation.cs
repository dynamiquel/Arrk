namespace Stackarr.Ui;

/// <summary>
/// The fixed top-level navigation sections. The shell renders them in this order; modules place
/// their entries into one of these sections rather than inventing new ones, so the navigation
/// stays consistent.
/// </summary>
public static class UiSections
{
    public const string Overview = "Overview";
    public const string Browse = "Browse";
    public const string System = "System";
    public const string Info = "Info";

    public static readonly string[] Order = [Overview, Browse, System, Info];
}

/// <summary>
/// A single navigation entry contributed by a module (or the shell). <see cref="Icon"/> is the
/// icon name resolved by the UI (MudBlazor <c>Icons.*</c> constants are plain strings).
/// </summary>
public sealed record NavigationEntry(
    string Section,
    string Label,
    string Href,
    string Icon,
    int Order = 100,
    bool AdminOnly = false,
    string? EnabledConfigPath = null,
    bool ExactMatch = false,
    string? LogoUrl = null);

/// <summary>
/// Contributes navigation entries. A module supplies its own entries, and its own
/// <see cref="NavigationEntry.EnabledConfigPath"/> (e.g. <c>services:tv</c>), so the entry
/// disappears live when the backing service is switched off, without the shell knowing which
/// modules exist.
/// </summary>
public interface INavigationProvider
{
    IReadOnlyList<NavigationEntry> Entries { get; }
}
