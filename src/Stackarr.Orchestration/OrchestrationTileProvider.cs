using MudBlazor;
using Stackarr.Orchestration.Components.Tiles;
using Stackarr.Ui;

namespace Stackarr.Orchestration;

/// <summary>
/// The orchestration module's tile contributions: service health, host load and Docker load. The
/// dashboard no longer hard-codes these; users place and size them in the Tile Editor.
/// </summary>
public sealed class OrchestrationTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        new(
            "services.status",
            "Services",
            "Status",
            Icons.Material.Filled.Speed,
            typeof(ServiceStatusTile),
            Description: "One live status block per service.",
            Href: "/docker",
            LinkAdminOnly: true,
            DefaultSize: TileSize.VeryLarge),
        new(
            "services.running",
            "Services",
            "Running",
            Icons.Material.Filled.Dns,
            typeof(ServiceStatTile),
            Description: "Services that are currently running.",
            Href: "/docker",
            LinkAdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "running" }),
        new(
            "services.healthy",
            "Services",
            "Healthy",
            Icons.Material.Filled.CheckCircle,
            typeof(ServiceStatTile),
            Description: "Healthy services out of the enabled total.",
            Href: "/docker",
            LinkAdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "healthy" }),
        new(
            "services.unhealthy",
            "Services",
            "Unhealthy",
            Icons.Material.Filled.Error,
            typeof(ServiceStatTile),
            Description: "Enabled services that need attention.",
            Href: "/docker",
            LinkAdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "unhealthy" }),
        new(
            "system.updated",
            "System",
            "Updated",
            Icons.Material.Filled.Schedule,
            typeof(ServiceStatTile),
            Description: "When service state was last collected.",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "updated" }),
        new(
            "system.host",
            "System",
            "Host",
            Icons.Material.Filled.Computer,
            typeof(HardwareUsageTile),
            Description: "Host CPU and memory from the last sample.",
            Parameters: new Dictionary<string, object?> { ["Source"] = "host" }),
        new(
            "docker.load",
            "Docker",
            "Usage",
            Icons.Material.Filled.ViewModule,
            typeof(HardwareUsageTile),
            Description: "Container CPU and memory totals.",
            Href: "/docker",
            AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Source"] = "docker" })
    ];
}
