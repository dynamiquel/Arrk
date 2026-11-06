using Arrk.Core.Services;
using Arrk.Docker;

namespace Arrk.Orchestration.Components.Shared;

/// <summary>
/// Shared container lifecycle actions (start/stop/restart) used by
/// ContainerList, ServiceView, InfrastructureView, and ContainerDetail.
/// Each method creates a short-lived Docker client, avoiding long-lived connections, and publishes a
/// live activity so the action shows up in the activity centre and clears itself when it finishes.
/// </summary>
public static class ContainerActionHelper
{
    public static Task StartContainer(
        string id, string name, LiveActivityRegistry activity, Func<Task>? afterAction = null) =>
        Run(activity, $"Starting {name}", "Starting the container", manager => manager.StartContainer(id), afterAction);

    public static Task StopContainer(
        string id, string name, LiveActivityRegistry activity, Func<Task>? afterAction = null) =>
        Run(activity, $"Stopping {name}", "Stopping the container", manager => manager.StopContainer(id), afterAction);

    public static Task RestartContainer(
        string id, string name, LiveActivityRegistry activity, Func<Task>? afterAction = null) =>
        Run(activity, $"Restarting {name}", "Restarting the container", manager => manager.RestartContainer(id), afterAction);

    static async Task Run(
        LiveActivityRegistry activity,
        string title,
        string detail,
        Func<ContainerManager, Task> action,
        Func<Task>? afterAction)
    {
        using LiveActivityScope scope = activity.Begin(LiveActivityKind.Container, title, detail);
        using global::Docker.DotNet.DockerClient client = DockerClientFactory.Create();
        ContainerManager manager = new(client);
        await action(manager);
        if (afterAction != null)
        {
            await afterAction();
        }
    }
}
