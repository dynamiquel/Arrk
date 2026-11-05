using Docker.DotNet;
using Stackarr.Core.Models;
using Stackarr.TestSupport;
using ContainerState = Stackarr.Core.Enums.ContainerState;

namespace Stackarr.Docker.Tests;

/// <summary>
/// Container lifecycle behaviour: create/start/stop/restart/remove, state transitions,
/// inspection and concurrent operations. Runs against a real Docker daemon.
/// </summary>
[NotInParallel("docker")]
[RequiresDocker]
public class ContainerLifecycleTests
{
    readonly List<string> _containerIds = [];
    readonly string _uid = Guid.NewGuid().ToString("N")[..8];

    [After(Test)]
    public async Task Cleanup()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        foreach (string id in _containerIds)
        {
            try { await containerManager.RemoveContainer(id, force: true); } catch { }
        }
        _containerIds.Clear();
    }

    string Name(string suffix) => $"stackarr-lc-{suffix}-{_uid}";

    async Task<string> StartContainerAsync(ContainerManager containerManager, string name, Dictionary<string, string>? labels = null)
    {
        string id = await DockerTestHelper.CreateContainer(DockerTestHelper.TestImage, name, labels);
        _containerIds.Add(id);
        return id;
    }

    [Test]
    public async Task CreateAndStart_Container_IsRunningWithExpectedName()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        await new ImageManager(client).PullImage(DockerTestHelper.TestImage);

        string name = Name("create");
        string id = await StartContainerAsync(containerManager, name, new() { ["stackarr.test"] = "lifecycle" });

        ContainerInfo? container = await containerManager.GetContainer(id);
        await Assert.That(container).IsNotNull();
        await Assert.That(container!.Name).IsEqualTo(name);
        await Assert.That(container.State).IsEqualTo(ContainerState.Running);
    }

    [Test]
    public async Task CreateWithoutStart_HasCreatedState()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        await new ImageManager(client).PullImage(DockerTestHelper.TestImage);

        string id = await containerManager.CreateContainerAsync(new()
        {
            Image = DockerTestHelper.TestImage,
            Name = Name("created")
        });
        _containerIds.Add(id);

        ContainerInfo? container = await containerManager.GetContainer(id);
        await Assert.That(container!.State).IsEqualTo(ContainerState.Created);
    }

    [Test]
    public async Task StopContainer_TransitionsToExited()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        string id = await StartContainerAsync(containerManager, Name("stop"));

        await containerManager.StopContainer(id);

        await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Exited);
    }

    [Test]
    public async Task RestartContainer_AfterStop_TransitionsBackToRunning()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        string id = await StartContainerAsync(containerManager, Name("restart"));

        await containerManager.StopContainer(id);
        await containerManager.RestartContainer(id);
        await Task.Delay(1000);

        await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Running);
    }

    [Test]
    public async Task RemoveContainer_NoLongerExists()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        string id = await StartContainerAsync(containerManager, Name("remove"));

        await containerManager.RemoveContainer(id, force: true);
        _containerIds.Remove(id);

        await Assert.That(await containerManager.GetContainer(id)).IsNull();
    }

    [Test]
    public async Task ForceRemove_RunningContainer_Succeeds()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        string id = await StartContainerAsync(containerManager, Name("forcerm"));

        await containerManager.RemoveContainer(id, force: true);
        _containerIds.Remove(id);

        await Assert.That(await containerManager.GetContainer(id)).IsNull();
    }

    [Test]
    public async Task FullLifecycle_CreateStartStopRestartRemove()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        string id = await StartContainerAsync(containerManager, Name("full"));

        await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Running);

        await containerManager.StopContainer(id);
        await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Exited);

        await containerManager.RestartContainer(id);
        await Task.Delay(1000);
        await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Running);

        await containerManager.RemoveContainer(id, force: true);
        _containerIds.Remove(id);
        await Assert.That(await containerManager.GetContainer(id)).IsNull();
    }

    [Test]
    public async Task InspectContainer_ReturnsDetailedInfo()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        string name = Name("inspect");
        string id = await StartContainerAsync(containerManager, name, new() { ["stackarr.test"] = "inspect" });

        var inspect = await containerManager.InspectContainer(id);

        await Assert.That(inspect.Name).IsEqualTo($"/{name}");
        await Assert.That(inspect.Config.Labels).ContainsKey("stackarr.test");
        await Assert.That(inspect.Config.Image).IsEqualTo(DockerTestHelper.TestImage);
    }

    [Test]
    public async Task GetContainer_ByPartialId_ReturnsContainer()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        string id = await StartContainerAsync(containerManager, Name("partial"));

        ContainerInfo? found = await containerManager.GetContainer(id[..12]);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(id);
    }

    [Test]
    public async Task GetContainer_Nonexistent_ReturnsNull()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);

        await Assert.That(await containerManager.GetContainer($"does-not-exist-{_uid}")).IsNull();
    }

    [Test]
    public async Task ListContainers_IncludesRunningAndCreatedStates()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        await new ImageManager(client).PullImage(DockerTestHelper.TestImage);

        string runningId = await StartContainerAsync(containerManager, Name("listrun"));
        string createdId = await containerManager.CreateContainerAsync(new()
        {
            Image = DockerTestHelper.TestImage,
            Name = Name("listcreated")
        });
        _containerIds.Add(createdId);

        List<ContainerInfo> targets = (await containerManager.ListContainers())
            .Where(c => c.Id == runningId || c.Id == createdId).ToList();

        await Assert.That(targets.Count).IsEqualTo(2);
        await Assert.That(targets.Select(c => c.State)).Contains(ContainerState.Running);
        await Assert.That(targets.Select(c => c.State)).Contains(ContainerState.Created);
    }

    [Test]
    public async Task ConcurrentStop_AllContainersExit()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);

        List<string> ids = [];
        for (int i = 0; i < 3; i++)
            ids.Add(await StartContainerAsync(containerManager, Name($"cstop{i}")));

        await Task.WhenAll(ids.Select(id => containerManager.StopContainer(id)));

        foreach (string id in ids)
            await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Exited);
    }

    [Test]
    public async Task ConcurrentRestart_AllContainersRunning()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);

        List<string> ids = [];
        for (int i = 0; i < 3; i++)
            ids.Add(await StartContainerAsync(containerManager, Name($"crestart{i}")));

        await Task.WhenAll(ids.Select(id => containerManager.RestartContainer(id)));
        await Task.Delay(1000);

        foreach (string id in ids)
            await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Running);
    }

    [Test]
    public async Task ConcurrentForceRemove_AllContainersGone()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);

        List<string> ids = [];
        for (int i = 0; i < 3; i++)
            ids.Add(await StartContainerAsync(containerManager, Name($"cremove{i}")));

        await Task.WhenAll(ids.Select(id => containerManager.RemoveContainer(id, force: true)));
        _containerIds.RemoveAll(ids.Contains);

        List<ContainerInfo> remaining = await containerManager.ListContainers();
        foreach (string id in ids)
            await Assert.That(remaining.Any(c => c.Id == id)).IsFalse();
    }

    [Test]
    public async Task RapidStopStartStop_ConvergesToExited()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        string id = await StartContainerAsync(containerManager, Name("rapid"));

        await containerManager.StopContainer(id);
        await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Exited);

        await containerManager.RestartContainer(id);
        await Task.Delay(1000);
        await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Running);

        await containerManager.StopContainer(id);
        await Assert.That((await containerManager.GetContainer(id))!.State).IsEqualTo(ContainerState.Exited);
    }
}
