using MudBlazor;
using Stackarr.Ui;

namespace Stackarr.Requests;

/// <summary>
/// The requests module's navigation. Gated by <c>requests:enabled</c>, so switching the request
/// system off hides the entry live, the shell never references the module to know this.
/// </summary>
public sealed class RequestsNavigation : INavigationProvider
{
    /// <summary>The config section every requests contribution is gated by.</summary>
    public const string EnabledConfigPath = "requests";

    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.Browse, "Requests", "/requests", Icons.Material.Filled.Inbox, Order: 40,
            EnabledConfigPath: EnabledConfigPath)
    ];
}
