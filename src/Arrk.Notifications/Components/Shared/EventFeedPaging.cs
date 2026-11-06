namespace Arrk.Notifications.Components.Shared;

/// <summary>
/// Client-side batching for the events feed. The current query window is loaded once (so category
/// filters keep working across the whole result set), but only one batch is rendered at a time and
/// the next batch is revealed when the user scrolls near the bottom.
/// </summary>
public static class EventFeedPaging
{
    public const int PageSize = 50;

    /// <summary>How many items are currently rendered, capped at the total available.</summary>
    public static int Visible(int shown, int total) =>
        Math.Clamp(shown, 0, Math.Max(total, 0));

    /// <summary>Reveals the next batch, stopping at the total available.</summary>
    public static int NextBatch(int shown, int total) =>
        Math.Min(Visible(shown, total) + PageSize, Math.Max(total, 0));
}
