namespace Stackarr.Ui.Components;

/// <summary>A single independent on/off filter rendered inside <see cref="StToggleChips{T}"/>.</summary>
/// <param name="Value">Value toggled when the chip is clicked.</param>
/// <param name="Label">Visible chip label.</param>
/// <param name="Icon">Optional MudBlazor icon (Icons.Material.*).</param>
/// <param name="Count">Optional count shown in a small pill.</param>
/// <param name="Title">Optional native tooltip.</param>
/// <param name="Disabled">When true the chip cannot be toggled.</param>
public sealed record StToggleChipItem<T>(
    T Value,
    string Label,
    string? Icon = null,
    string? Count = null,
    string? Title = null,
    bool Disabled = false);
