using MudBlazor;
using Stackarr.Ui;
using Stackarr.WatchActivity.Components.Tiles;

namespace Stackarr.WatchActivity;

/// <summary>Per-account watch/listen history counters, shared by the History page and the dashboard.</summary>
public sealed class WatchActivityTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        new(
            "history.plays",
            "History",
            "Plays",
            Icons.Material.Filled.PlayCircle,
            typeof(WatchStatTile),
            Description: "Playback events recorded for this account.",
            Href: "/history",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "plays" }),
        new(
            "history.latest",
            "History",
            "Latest",
            Icons.Material.Filled.Schedule,
            typeof(WatchStatTile),
            Description: "When this account last watched or listened.",
            Href: "/history",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "latest" })
    ];
}
