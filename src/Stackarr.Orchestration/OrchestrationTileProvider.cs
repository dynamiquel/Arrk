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
            Order: 10,
            Description: "One live status block per service.",
            Href: "/docker",
            LinkAdminOnly: true,
            DefaultSize: TileSize.Large,
            DefaultSection: "Overview",
            DefaultOrder: 10),
        new(
            "services.running",
            "Services",
            "Running",
            Icons.Material.Filled.Dns,
            typeof(ServiceStatTile),
            Order: 20,
            Description: "Services that are currently running.",
            Href: "/docker",
            LinkAdminOnly: true,
            DefaultSection: "Overview",
            DefaultOrder: 20,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "running" }),
        new(
            "services.healthy",
            "Services",
            "Healthy",
            Icons.Material.Filled.CheckCircle,
            typeof(ServiceStatTile),
            Order: 30,
            Description: "Healthy services out of the enabled total.",
            Href: "/docker",
            LinkAdminOnly: true,
            DefaultSection: "Overview",
            DefaultOrder: 30,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "healthy" }),
        new(
            "services.unhealthy",
            "Services",
            "Unhealthy",
            Icons.Material.Filled.Error,
            typeof(ServiceStatTile),
            Order: 40,
            Description: "Enabled services that need attention.",
            Href: "/docker",
            LinkAdminOnly: true,
            DefaultSection: "Overview",
            DefaultOrder: 40,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "unhealthy" }),
        new(
            "system.updated",
            "System",
            "Updated",
            Icons.Material.Filled.Schedule,
            typeof(ServiceStatTile),
            Order: 50,
            Description: "When service state was last collected.",
            DefaultSection: "Overview",
            DefaultOrder: 50,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "updated" }),
        new(
            "system.host",
            "System",
            "Host",
            Icons.Material.Filled.Computer,
            typeof(HardwareUsageTile),
            Order: 60,
            Description: "Host CPU and memory from the last sample.",
            DefaultSection: "Overview",
            DefaultOrder: 60,
            Parameters: new Dictionary<string, object?> { ["Source"] = "host" }),
        new(
            "docker.load",
            "Docker",
            "Usage",
            Icons.Material.Filled.ViewModule,
            typeof(HardwareUsageTile),
            Order: 10,
            Description: "Container CPU and memory totals.",
            Href: "/docker",
            AdminOnly: true,
            DefaultSection: "Overview",
            DefaultOrder: 70,
            Parameters: new Dictionary<string, object?> { ["Source"] = "docker" })
    ];
}
