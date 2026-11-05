using MudBlazor;
using Stackarr.Ai.Components.Tiles;
using Stackarr.Ui;

namespace Stackarr.Ai;

/// <summary>Admin-only AI usage counters, contributed to the shared tile catalogue.</summary>
public sealed class AiTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        Create("ai.calls", "Calls", Icons.Material.Filled.Bolt, "calls", 10),
        Create("ai.tokens", "Tokens", Icons.Material.Filled.DataUsage, "tokens", 20),
        Create("ai.failures", "Failures", Icons.Material.Filled.ErrorOutline, "failures", 30),
        Create("ai.cache", "Cache", Icons.Material.Filled.Cached, "cache", 40)
    ];

    static TileDefinition Create(string id, string title, string icon, string metric, int order) =>
        new(
            id,
            "AI",
            title,
            icon,
            typeof(AiUsageStatTile),
            Order: order,
            Description: $"{title} over the last 30 days.",
            Href: "/ai",
            AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = metric });
}
