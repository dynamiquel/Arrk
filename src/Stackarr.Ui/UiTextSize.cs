using Stackarr.Core.Services;
namespace Stackarr.Ui;

/// <summary>Per-account UI text size. Every size in the UI is relative to the root font size,
/// so this scales the whole shell without changing layout metrics.</summary>
public enum UiTextSize
{
    Small,
    Medium,
    Large,
}

/// <summary>Reads and writes the per-account UI text size. The value lives in
/// <see cref="SettingsService"/>; the shell mirrors it to a cookie so it can be applied before
/// the first paint.</summary>
public sealed class UiTextSizePreference(SettingsService settings)
{
    public const string CookieName = "st-text-size";

    /// <summary>Cookie/CSS value for a size (<c>small</c>, <c>medium</c>, <c>large</c>).</summary>
    public static string ToValue(UiTextSize size) => size switch
    {
        UiTextSize.Small => "small",
        UiTextSize.Large => "large",
        _ => "medium",
    };

    /// <summary>Parses a cookie/CSS value; anything unknown is Medium.</summary>
    public static UiTextSize Parse(string? value) => value switch
    {
        "small" => UiTextSize.Small,
        "large" => UiTextSize.Large,
        _ => UiTextSize.Medium,
    };

    public async Task<UiTextSize> GetAsync(int userId, CancellationToken ct = default)
    {
        string? value = await settings.GetAsync(Key(userId), ct);
        return Parse(value);
    }

    public Task SetAsync(int userId, UiTextSize size, CancellationToken ct = default) =>
        settings.SetAsync(Key(userId), ToValue(size), ct);

    static string Key(int userId) => $"ui.text-size.{userId}";
}
