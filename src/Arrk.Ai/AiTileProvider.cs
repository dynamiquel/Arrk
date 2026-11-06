using MudBlazor;
using Arrk.Ai.Components.Tiles;
using Arrk.Ui;

namespace Arrk.Ai;

/// <summary>Admin-only AI usage counters, contributed to the shared tile catalogue.</summary>
public sealed class AiTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        new(
            "ai.calls", "AI", "Calls", Icons.Material.Filled.Bolt, typeof(AiUsageStatTile),
            Description: "Calls over the last 30 days.", Href: "/ai", AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "calls" }),
        new(
            "ai.tokens", "AI", "Tokens", Icons.Material.Filled.DataUsage, typeof(AiUsageStatTile),
            Description: "Tokens over the last 30 days.", Href: "/ai", AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "tokens" }),
        new(
            "ai.failures", "AI", "Failures", Icons.Material.Filled.ErrorOutline, typeof(AiUsageStatTile),
            Description: "Failures over the last 30 days.", Href: "/ai", AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "failures" }),
        new(
            "ai.cache", "AI", "Cache", Icons.Material.Filled.Cached, typeof(AiUsageStatTile),
            Description: "Cache over the last 30 days.", Href: "/ai", AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "cache" })
    ];
}
