namespace Arrk.Ui.Components;

/// <summary>A single tab rendered inside <see cref="StTabs{T}"/>.</summary>
/// <param name="Value">Value passed back through <c>ValueChanged</c> when selected.</param>
/// <param name="Label">Visible tab label.</param>
/// <param name="Icon">Optional MudBlazor icon (Icons.Material.*).</param>
/// <param name="Count">Optional count shown in a small pill.</param>
/// <param name="Disabled">When true the tab cannot be selected.</param>
/// <param name="CountVariant">Optional colour for the count pill (e.g. <c>success</c>, <c>warning</c>).</param>
/// <param name="Title">Optional native tooltip shown on hover (e.g. why the tab is disabled).</param>
public sealed record StTabItem<T>(
    T Value,
    string Label,
    string? Icon = null,
    string? Count = null,
    bool Disabled = false,
    string? CountVariant = null,
    string? Title = null,
    string? IconUrl = null);
