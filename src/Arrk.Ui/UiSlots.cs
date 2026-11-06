namespace Arrk.Ui;

/// <summary>
/// Well-known shell regions where modules may inject components. Modules are free to define their
/// own region ids alongside these (e.g. <c>tv.series.actions</c>); a region only exists where the
/// owning component renders a <c>&lt;UiRegion&gt;</c>.
/// </summary>
public static class UiRegions
{
    /// <summary>The app bar action cluster (notification bell, etc.).</summary>
    public const string ShellAppBarActions = "shell.appbar.actions";

    /// <summary>Top-level overlays rendered outside the layout (music mini-player, etc.).</summary>
    public const string ShellOverlays = "shell.overlays";

    /// <summary>Behaviour-only overlays inside the full-screen player (media detail dialogs, etc.).</summary>
    public const string PlayerOverlays = "player.overlays";

    /// <summary>Action buttons in the Discover page header (each module's "My …" quick links).</summary>
    public const string DiscoverActions = "discover.actions";

    /// <summary>Admin alert sections on the Dashboard (pending accounts, ...).</summary>
    public const string DashboardAlerts = "dashboard.alerts";

    /// <summary>External account connections on the My account page (MyAnimeList, ...).</summary>
    public const string AccountConnections = "account.connections";
}

public enum UiSlotMode
{
    /// <summary>Render before the host's own content in the region.</summary>
    Prepend,
    /// <summary>Render after the host's own content in the region.</summary>
    Append,
    /// <summary>Render instead of everything else in the region (highest order wins the region).</summary>
    Replace
}

/// <summary>
/// A component injected into a named region. <see cref="Region"/> may contain a <c>*</c> wildcard
/// to target several regions (e.g. <c>tv.*.actions</c>).
/// </summary>
public sealed record UiSlot(
    string Region,
    Type Component,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    int Order = 100,
    bool AdminOnly = false,
    string? EnabledConfigPath = null,
    UiSlotMode Mode = UiSlotMode.Append);

/// <summary>Contributes slot components to regions. Modules register it in DI.</summary>
public interface IUiSlotProvider
{
    IReadOnlyList<UiSlot> Slots { get; }
}

/// <summary>
/// Attribute form of a slot contribution: decorate a component and it is discovered automatically,
/// no provider class needed. This is the lightweight path for add-on modules.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UiRegionAttribute(string region) : Attribute
{
    public string Region { get; } = region;
    public int Order { get; set; } = 100;
    public bool AdminOnly { get; set; }

    /// <summary>Optional config section path whose <c>enabled</c> flag gates this contribution.</summary>
    public string? EnabledConfigPath { get; set; }

    public UiSlotMode Mode { get; set; } = UiSlotMode.Append;
}
