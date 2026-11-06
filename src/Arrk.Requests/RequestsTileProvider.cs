using MudBlazor;
using Arrk.Requests.Components.Tiles;
using Arrk.Ui;

namespace Arrk.Requests;

/// <summary>The request system's headline counts and pending queue, contributed as shareable tiles.</summary>
public sealed class RequestsTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        new(
            "requests.open", "Requests", "Open", Icons.Material.Filled.PendingActions, typeof(RequestsStatTile),
            Description: "Open media requests.", Href: "/requests",
            EnabledConfigPath: "requests",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "open" }),
        new(
            "requests.accepted", "Requests", "Accepted", Icons.Material.Filled.CheckCircle, typeof(RequestsStatTile),
            Description: "Accepted media requests.", Href: "/requests",
            EnabledConfigPath: "requests",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "accepted" }),
        new(
            "requests.denied", "Requests", "Denied", Icons.Material.Filled.Cancel, typeof(RequestsStatTile),
            Description: "Denied media requests.", Href: "/requests",
            EnabledConfigPath: "requests",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "denied" }),
        new(
            "requests.blacklisted", "Requests", "Blacklisted", Icons.Material.Filled.Block, typeof(RequestsStatTile),
            Description: "Blacklisted media requests.", Href: "/requests",
            EnabledConfigPath: "requests",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "blacklisted" }),
        new(
            "requests.total", "Requests", "Total", Icons.Material.Filled.AllInbox, typeof(RequestsStatTile),
            Description: "Total media requests.", Href: "/requests",
            EnabledConfigPath: "requests",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "total" }),

        // The Requests page's pending queue as an interactive shelf.
        new(
            "requests.pending", "Requests", "Pending Requests", Icons.Material.Filled.HourglassTop,
            typeof(PendingRequestsTile), Description: "Requests waiting for a decision, with approve and deny actions.",
            EnabledConfigPath: "requests", DefaultSize: TileSize.VeryLarge)
    ];
}
