using Arrk.Tvarr.Components.Shared;
using Arrk.Ui;

namespace Arrk.Tvarr;

/// <summary>
/// The TV module's slot contributions: the "My Movies / My Shows" quick links injected into the
/// Discover page header through the <c>discover.actions</c> region. Gated by
/// <c>services:tv:enabled</c>.
/// </summary>
public sealed class TvShellSlots : IUiSlotProvider
{
    public IReadOnlyList<UiSlot> Slots { get; } =
    [
        new(UiRegions.DiscoverActions, typeof(TvDiscoverActions), Order: 10, EnabledConfigPath: TvNavigation.EnabledConfigPath)
    ];
}
