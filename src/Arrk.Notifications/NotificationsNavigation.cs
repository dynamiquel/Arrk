using MudBlazor;
using Arrk.Ui;

namespace Arrk.Notifications;

/// <summary>
/// The notifications module's navigation: the All Events page. The notification inbox itself is a
/// slot (the app-bar bell), not a page, so only the event log appears in the menu.
/// </summary>
public sealed class NotificationsNavigation : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.Browse, "Events", "/events", Icons.Material.Filled.EventNote, Order: 42)
    ];
}
