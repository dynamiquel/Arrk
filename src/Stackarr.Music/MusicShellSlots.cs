using Stackarr.Music.Components.Shared;
using Stackarr.Ui;

namespace Stackarr.Music;

/// <summary>
/// The music module's slot contributions: the mini/full player overlay (injected through the
/// <c>shell.overlays</c> region) and the "My Music" quick link in the Discover page header. Gated by
/// <c>services:music:enabled</c>, so the shell layout never references the module.
/// </summary>
public sealed class MusicShellSlots : IUiSlotProvider
{
    public IReadOnlyList<UiSlot> Slots { get; } =
    [
        new(UiRegions.ShellOverlays, typeof(MusicPlayer), Order: 10, EnabledConfigPath: MusicNavigation.EnabledConfigPath),
        new(UiRegions.DiscoverActions, typeof(MusicDiscoverActions), Order: 20, EnabledConfigPath: MusicNavigation.EnabledConfigPath)
    ];
}
