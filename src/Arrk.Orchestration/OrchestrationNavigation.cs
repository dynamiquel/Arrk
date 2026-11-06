using MudBlazor;
using Arrk.Ui;

namespace Arrk.Orchestration;

/// <summary>
/// The orchestration module's navigation: the Docker view and the Scheduler. Both are admin-only and
/// always available, so they are not config-gated.
/// </summary>
public sealed class OrchestrationNavigation : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.System, "Docker", "/docker", Icons.Material.Filled.ViewModule, Order: 10, AdminOnly: true),
        new(UiSections.System, "Scheduler", "/scheduler", Icons.Material.Filled.Schedule, Order: 13, AdminOnly: true)
    ];
}
