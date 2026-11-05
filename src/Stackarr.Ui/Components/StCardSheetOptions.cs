using MudBlazor;

namespace Stackarr.Ui.Components;

/// <summary>
/// Consistent <see cref="DialogOptions"/> for the floating "card sheet" overlays
/// (navigation grid, notification inbox, account menu). Card sheets always close on
/// backdrop click, Escape and navigation.
/// </summary>
public static class StCardSheetOptions
{
    /// <summary>
    /// Centred card sheet (navigation grid). On desktop it floats in from the top centre;
    /// on mobile CSS centres it as a near-full-width sheet.
    /// </summary>
    public static DialogOptions Create(MaxWidth maxWidth = MaxWidth.Small) =>
        Base(maxWidth, DialogPosition.TopCenter);

    /// <summary>
    /// App-bar anchored card sheet (notification inbox, account menu). On desktop it drops
    /// down under the button that opened it (top-right); on mobile CSS centres it.
    /// </summary>
    public static DialogOptions CreateAnchored(MaxWidth maxWidth = MaxWidth.Small) =>
        Base(maxWidth, DialogPosition.TopRight);

    static DialogOptions Base(MaxWidth maxWidth, DialogPosition position) => new()
    {
        MaxWidth = maxWidth,
        FullWidth = true,
        CloseOnEscapeKey = true,
        BackdropClick = true,
        CloseOnNavigation = true,
        NoHeader = true,
        CloseButton = false,
        Position = position,
    };
}
