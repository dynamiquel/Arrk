using Docker.DotNet;
using Arrk.Core.Models;
using Arrk.TestSupport;

namespace Arrk.Docker.Tests;

/// <summary>
/// Querying containers by label, name and image, and verifying label round-tripping.
/// </summary>
[NotInParallel("docker")]
[RequiresDocker]
public class ContainerQueryTests
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

    string Name(string suffix) => $"stackarr-q-{suffix}-{_uid}";

    async Task<string> StartAsync(ContainerManager containerManager, string name, Dictionary<string, string>? labels = null)
    {
        string id = await DockerTestHelper.CreateContainer(DockerTestHelper.TestImage, name, labels);
        _containerIds.Add(id);
        return id;
    }

    [Test]
    public async Task Labels_AreSetOnCreation_AndRoundTripThroughInspect()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        await new ImageManager(client).PullImage(DockerTestHelper.TestImage);

        Dictionary<string, string> labels = new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
            [ContainerLabels.Service] = "radarr",
            ["arrk.environment"] = "test"
        };
        string id = await StartAsync(containerManager, Name("labels"), labels);

        var inspect = await containerManager.InspectContainer(id);
        foreach (KeyValuePair<string, string> kvp in labels)
        {
            await Assert.That(inspect.Config.Labels).ContainsKey(kvp.Key);
            await Assert.That(inspect.Config.Labels[kvp.Key]).IsEqualTo(kvp.Value);
        }
    }

    [Test]
    public async Task ContainerInfo_ContainsLabels()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        await new ImageManager(client).PullImage(DockerTestHelper.TestImage);

        string id = await StartAsync(containerManager, Name("info"), new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
            [ContainerLabels.Service] = "prowlarr"
        });

        ContainerInfo? container = await containerManager.GetContainer(id);

        await Assert.That(container!.Labels[ContainerLabels.Managed]).IsEqualTo(ContainerLabels.ManagedValue);
        await Assert.That(container.Labels[ContainerLabels.Service]).IsEqualTo("prowlarr");
    }

    [Test]
    public async Task ListContainers_ByLabelFilter_ReturnsOnlyMatching()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        await new ImageManager(client).PullImage(DockerTestHelper.TestImage);

        string matching = await StartAsync(containerManager, Name("filter-yes"), new() { ["arrk.filter"] = "yes" });
        string nonMatching = await StartAsync(containerManager, Name("filter-no"), new() { ["arrk.filter"] = "no" });

        List<ContainerInfo> filtered = await containerManager.ListContainers(labelFilter: "arrk.filter=yes");

        await Assert.That(filtered.Any(c => c.Id == matching)).IsTrue();
        await Assert.That(filtered.Any(c => c.Id == nonMatching)).IsFalse();
    }

    [Test]
    public async Task ListContainersByLabels_MatchesAllLabels()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        await new ImageManager(client).PullImage(DockerTestHelper.TestImage);

        string target = await StartAsync(containerManager, Name("multilabel-match"), new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
            [ContainerLabels.Service] = "sonarr"
        });
        string other = await StartAsync(containerManager, Name("multilabel-other"), new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
            [ContainerLabels.Service] = "radarr"
        });

        List<ContainerInfo> matched = await containerManager.ListContainersByLabels(new()
        {
            [ContainerLabels.Service] = "sonarr"
        });

        await Assert.That(matched.Any(c => c.Id == target)).IsTrue();
        await Assert.That(matched.Any(c => c.Id == other)).IsFalse();
    }

    [Test]
    public async Task GetContainersByImageAsync_MatchesByImageName()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        await new ImageManager(client).PullImage(DockerTestHelper.TestImage);

        string id = await StartAsync(containerManager, Name("byimage"));

        List<ContainerInfo> byExact = await containerManager.GetContainersByImageAsync(DockerTestHelper.TestImage);
        List<ContainerInfo> byNameOnly = await containerManager.GetContainersByImageAsync("nginx");

        await Assert.That(byExact.Any(c => c.Id == id)).IsTrue();
        await Assert.That(byNameOnly.Any(c => c.Id == id)).IsTrue();
    }

    [Test]
    public async Task GetContainersByImageAsync_UnknownImage_ReturnsEmpty()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);

        List<ContainerInfo> containers = await containerManager.GetContainersByImageAsync("nonexistent-image-does-not-exist:latest");

        await Assert.That(containers).IsEmpty();
    }

    [Test]
    public async Task Labels_DistinguishServices()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ContainerManager containerManager = new(client);
        await new ImageManager(client).PullImage(DockerTestHelper.TestImage);

        string radarrId = await StartAsync(containerManager, Name("svc-radarr"), new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
            [ContainerLabels.Service] = "radarr"
        });
        string sonarrId = await StartAsync(containerManager, Name("svc-sonarr"), new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
            [ContainerLabels.Service] = "sonarr"
        });

        var radarrInspect = await containerManager.InspectContainer(radarrId);
        var sonarrInspect = await containerManager.InspectContainer(sonarrId);

        await Assert.That(radarrInspect.Config.Labels[ContainerLabels.Service]).IsEqualTo("radarr");
        await Assert.That(sonarrInspect.Config.Labels[ContainerLabels.Service]).IsEqualTo("sonarr");
    }
}
