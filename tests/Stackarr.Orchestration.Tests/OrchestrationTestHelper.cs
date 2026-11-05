using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Docker;
using Stackarr.TestSupport;

using Stackarr.Core.Configuration;

namespace Stackarr.Orchestration.Tests;

public static class OrchestrationTestHelper
{
    public static DockerClient CreateClient() => DockerClientFactory.Create();

    public static ContainerManager CreateContainerManager(DockerClient client) => new(client);

    public static NetworkManager CreateNetworkManager(DockerClient client) => new(client);

    public static ImageManager CreateImageManager(DockerClient client) => new(client);

    public static ILogger<T> CreateLogger<T>()
    {
        return new LoggerFactory().CreateLogger<T>();
    }

    public static IDbContextFactory<StackarrDbContext> CreateInMemoryDbFactory(bool initializeSchema = false)
    {
        // Use a real SQLite database (temp file) because production code calls relational-only APIs
        // such as Database.MigrateAsync(). PendingModelChangesWarning is suppressed because these
        // tests build the schema directly instead of applying migrations.
        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-test-{Guid.NewGuid():N}.db");
        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        if (initializeSchema)
        {
            using StackarrDbContext context = new(options, []);
            context.Database.EnsureCreated();
        }

        return new TestDbContextFactory(options);
    }

    /// <summary>
    /// All test instances use a stack name prefixed with <c>test-</c> so that any resources
    /// they leak (containers, named volumes, networks) can be safely identified and removed
    /// by <see cref="DockerTestCleanup"/> without touching the running application's resources.
    /// </summary>
    public static StackarrInstance CreateStackarrInstance(string? stackName = null)
    {
        string baseName = stackName ?? Guid.NewGuid().ToString("N")[..8];

        // Idempotent: helpers are often handed an already run-prefixed StackName.
        string name;
        if (baseName.StartsWith(TestRun.InstancePrefix, StringComparison.Ordinal))
        {
            name = baseName;
        }
        else
        {
            if (baseName.StartsWith("test-", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[5..];
            name = $"{TestRun.InstancePrefix}-{baseName}";
        }

        ConfigurationBuilder cb = new();
        // StackarrInstance writes the generated StackName back into the configuration root,
        // which requires at least one provider to be registered.
        cb.AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = name });
        IConfiguration configuration = cb.Build();
        // Share one network across the whole suite: creating a network per instance would exhaust
        // Docker's small default address pool. Container names stay run-scoped.
        return new StackarrInstance(configuration, networkName: TestRun.SharedNetworkName);
    }

    public static ResourceInventory CreateResourceInventory(
        ContainerManager containerManager,
        NetworkManager networkManager,
        string? instanceId = null)
    {
        DockerClient client = CreateClient();
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        StackarrInstance stackarr = CreateStackarrInstance(instanceId);
        return new(
            containerManager,
            networkManager,
            metricsCollector,
            client,
            stackarr,
            CreateLogger<ResourceInventory>());
    }

    public static ServiceProvisioner CreateServiceProvisioner(
        ContainerManager containerManager,
        NetworkManager networkManager,
        ImageManager imageManager,
        ResourceInventory? inventory = null,
        string? instanceId = null,
        LiveActivityRegistry? liveActivities = null)
    {
        StackarrInstance stackarr = CreateStackarrInstance(instanceId);
        inventory ??= CreateResourceInventory(containerManager, networkManager, instanceId);
        MutableOptionsMonitor<StackarrConfig> optionsMonitor = new MutableOptionsMonitor<StackarrConfig>(new());
        return new(
            containerManager,
            networkManager,
            imageManager,
            inventory,
            optionsMonitor,
            stackarr,
            liveActivities ?? new LiveActivityRegistry(),
            CreateLogger<ServiceProvisioner>());
    }

    public static StackarrProvisioner CreateStackarrProvisioner(
        NetworkManager networkManager,
        ImageManager imageManager,
        IDbContextFactory<StackarrDbContext>? dbFactory = null)
    {
        dbFactory ??= CreateInMemoryDbFactory();
        StackarrInstance stackarr = CreateStackarrInstance();
        return new(
            networkManager,
            imageManager,
            dbFactory,
            TestHelper.CreateDefault(),
            TestHelper.EmptyConfiguration(),
            stackarr,
            CreateLogger<StackarrProvisioner>());
    }

    /// <summary>
    /// Polls until the named container reaches the expected state (or times out).
    /// Returns the last observed container (may be null / wrong state on timeout).
    /// </summary>
    public static async Task<ContainerInfo?> WaitForContainerState(
        ContainerManager containerManager,
        string name,
        Core.Enums.ContainerState expected,
        TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(20);
        DateTime deadline = DateTime.UtcNow + limit;
        ContainerInfo? container = null;

        while (DateTime.UtcNow < deadline)
        {
            container = await containerManager.GetContainer(name);
            if (container != null && container.State == expected)
                return container;
            await Task.Delay(250);
        }

        return container;
    }

    /// <summary>
    /// Polls until the instance owns a container with the given name (or times out).
    /// </summary>
    public static async Task<List<ContainerInfo>> WaitForOwnedContainers(
        ResourceInventory inventory,
        Func<List<ContainerInfo>, bool> predicate,
        TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(20);
        DateTime deadline = DateTime.UtcNow + limit;
        List<ContainerInfo> owned = [];

        while (DateTime.UtcNow < deadline)
        {
            owned = await inventory.GetOwnedContainers();
            if (predicate(owned))
                return owned;
            await Task.Delay(250);
        }

        return owned;
    }

    public static async Task<string> CreateContainerOnNetwork(
        DockerClient client,
        string image,
        string name,
        string networkId,
        Dictionary<string, string>? labels = null,
        int? hostPort = null,
        string containerPort = "80/tcp")
    {
        CreateContainerParameters parameters = new()
        {
            Image = image,
            Name = name,
            Labels = MergeRunLabels(labels),
            NetworkingConfig = new()
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkId] = new()
                }
            }
        };

        if (hostPort.HasValue)
        {
            parameters.HostConfig = new()
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [containerPort] = [new() { HostPort = hostPort.Value.ToString() }]
                }
            };
        }

        CreateContainerResponse? response = await client.Containers.CreateContainerAsync(parameters);
        ContainerStartParameters startParams = new();
        await client.Containers.StartContainerAsync(response.ID, startParams);
        return response.ID;
    }

    static Dictionary<string, string> MergeRunLabels(Dictionary<string, string>? labels)
    {
        Dictionary<string, string> merged = labels is null
            ? new Dictionary<string, string> { [ContainerLabels.Managed] = ContainerLabels.ManagedValue }
            : new Dictionary<string, string>(labels);
        merged["stackarr.test"] = "true";
        merged[TestRun.RunLabel] = TestRun.Id;
        return merged;
    }
}

internal class TestDbContextFactory(DbContextOptions<StackarrDbContext> options) : IDbContextFactory<StackarrDbContext>
{
    public StackarrDbContext CreateDbContext() => new(options, []);

    public ValueTask<StackarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new StackarrDbContext(options, []));
}
