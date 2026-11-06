using Arrk.Auth.Components.Shared;
using Arrk.Ui;

namespace Arrk.Auth;

public sealed class AuthShellSlots : IUiSlotProvider
{
    public IReadOnlyList<UiSlot> Slots { get; } =
    [
        new(UiRegions.ShellAppBarActions, typeof(LoginDisplay), Order: 10),
        new(UiRegions.DashboardAlerts, typeof(PendingAccounts), Order: 10, AdminOnly: true)
    ];
}
