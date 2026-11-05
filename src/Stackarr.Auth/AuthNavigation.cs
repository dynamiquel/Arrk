using MudBlazor;
using Stackarr.Ui;

namespace Stackarr.Auth;

public sealed class AuthNavigation : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.System, "Users", "/users", Icons.Material.Filled.ManageAccounts, Order: 14, AdminOnly: true)
    ];
}
