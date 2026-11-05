using MudBlazor;
using Stackarr.Requests.Components.Tiles;
using Stackarr.Ui;

namespace Stackarr.Requests;

/// <summary>The request system's headline counts, contributed as shareable tiles.</summary>
public sealed class RequestsTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        Create("requests.open", "Open", Icons.Material.Filled.PendingActions, "open", 10, null),
        Create("requests.accepted", "Accepted", Icons.Material.Filled.CheckCircle, "accepted", 20, null),
        Create("requests.denied", "Denied", Icons.Material.Filled.Cancel, "denied", 30, null),
        Create("requests.blacklisted", "Blacklisted", Icons.Material.Filled.Block, "blacklisted", 40, null),
        Create("requests.total", "Total", Icons.Material.Filled.AllInbox, "total", 50, null)
    ];

    static TileDefinition Create(string id, string title, string icon, string metric, int order, string? defaultSection) =>
        new(
            id,
            "Requests",
            title,
            icon,
            typeof(RequestsStatTile),
            Order: order,
            Description: $"{title} media requests.",
            Href: "/requests",
            EnabledConfigPath: "requests",
            DefaultSection: defaultSection,
            Parameters: new Dictionary<string, object?> { ["Metric"] = metric });
}
