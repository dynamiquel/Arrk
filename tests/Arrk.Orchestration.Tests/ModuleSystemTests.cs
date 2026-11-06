using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.App;
using Arrk.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Modules;
using Arrk.Core.Notifications;
using Arrk.Core.Services;
using Arrk.DownloadManagement;
using Arrk.Speedtest;
using Arrk.Tvarr;

namespace Arrk.Orchestration.Tests;

public class ModuleSystemTests
{
    [Test]
    public async Task ModuleLoader_BuildRegistry_IncludesCustomModuleServices()
    {
        ModuleLoader loader = TestHelper.CreateModuleLoader();
        loader.RegisterModule(new TestModule());
        ServiceRegistry registry = loader.BuildRegistry();

        await Assert.That(registry.Contains("test_service")).IsTrue();
        await Assert.That(registry.All.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task ModuleLoader_ConfigureAllServices_InvokesEachModule()
    {
        TestModule.ConfigureServicesCalled = false;
        ModuleLoader loader = TestHelper.CreateModuleLoader();
        loader.RegisterModule(new TestModule());
        IConfigurationRoot configuration = new ConfigurationBuilder().Build();

        loader.ConfigureAllServices(new ServiceCollection(), configuration);

        await Assert.That(TestModule.ConfigureServicesCalled).IsTrue();
    }

    [Test]
    public async Task ModuleLoader_ConfigureAllHostedServices_InvokesEachModule()
    {
        TestModule.ConfigureHostedServicesCalled = false;
        ModuleLoader loader = TestHelper.CreateModuleLoader();
        loader.RegisterModule(new TestModule());

        loader.ConfigureAllHostedServices(new ServiceCollection());

        await Assert.That(TestModule.ConfigureHostedServicesCalled).IsTrue();
    }

    [Test]
    public async Task ModuleLoader_ScanAssembly_FindsModules()
    {
        ModuleLoader loader = TestHelper.CreateModuleLoader();
        loader.ScanAssembly(typeof(Arrk.Tvarr.TvModule).Assembly);

        await Assert.That(loader.Modules.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task CustomModule_ServiceSpec_BuildsContainers()
    {
        ServiceRegistry registry = TestHelper.CreateFromModules(new TestModule());
        ArrkConfig config = ConfigDefaults.GetDefaults();
        IConfigurationRoot configuration = new ConfigurationBuilder().Build();
        VolumePathResolver resolver = new(config.Storage);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        Service service = registry.GetRequired("test_service");
        IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, resolver, arrk);

        await Assert.That(specs.Count).IsEqualTo(1);
        await Assert.That(specs[0].Image).IsEqualTo("linuxserver/test:latest");
    }

    [Test]
    public async Task ModuleLoader_MultipleCustomModules_AllRegister()
    {
        ServiceRegistry registry = TestHelper.CreateFromModules(new TestModule(), new TestModule2());

        await Assert.That(registry.All.Count()).IsEqualTo(2);
        await Assert.That(registry.Contains("test_service")).IsTrue();
        await Assert.That(registry.Contains("test_service_2")).IsTrue();
    }

    [Test]
    public async Task ModuleLoader_RegisterConfigSections_AggregatesModuleSections()
    {
        ModuleLoader loader = TestHelper.CreateModuleLoader();
        loader.RegisterModule(new ConfigModule());
        ConfigSectionRegistry sectionRegistry = new();

        loader.RegisterConfigSections(sectionRegistry);

        await Assert.That(sectionRegistry.FindByPath("test_section")).IsNotNull();
    }

    [Test]
    public async Task NotificationRegistry_AggregatesAllModuleProviders()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<ArrkDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        services.AddArrkCore();

        ModuleLoader loader = ServiceRegistryFactory.CreateModuleLoader();
        loader.ConfigureAllServices(services, new ConfigurationBuilder().Build());

        using ServiceProvider provider = services.BuildServiceProvider();
        NotificationEventRegistry registry = provider.GetRequiredService<NotificationEventRegistry>();

        // One event from each domain, contributed by its owning module.
        await Assert.That(registry.IsKnown(DownloadNotificationTopics.DownloadCompleted)).IsTrue();
        await Assert.That(registry.IsKnown(HealthNotificationTopics.ServiceDown)).IsTrue();
        await Assert.That(registry.IsKnown(StorageNotificationTopics.StorageLow)).IsTrue();
        await Assert.That(registry.IsKnown(SpeedtestNotificationTopics.SpeedtestDegraded)).IsTrue();
        await Assert.That(registry.IsKnown(MediaNotificationTopics.MediaAdded)).IsTrue();
    }

    class TestModule : IArrkModule
    {
        public static bool ConfigureServicesCalled { get; set; }
        public static bool ConfigureHostedServicesCalled { get; set; }

        public string ModuleName => "TestModule";

        public IReadOnlyList<Service> GetServices() =>
        [
            new(new TestServiceSpec
            {
                Key = "test_service",
                DisplayName = "Test Service",
                Description = "A test service for module system tests",
                IsInfrastructure = false,
                IsEnabled = _ => true,
                BuildContainerSpecs = (_, _, arrk) =>
                [
                    new()
                    {
                        Image = "linuxserver/test:latest",
                        Name = arrk.GetContainerName("test"),
                        Labels = new() { [ContainerLabels.Service] = "test_service" },
                        RestartPolicy = RestartPolicyType.UnlessStopped
                    }
                ]
            })
        ];

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            ConfigureServicesCalled = true;
        }

        public void ConfigureHostedServices(IServiceCollection services)
        {
            ConfigureHostedServicesCalled = true;
        }
    }

    class TestModule2 : IArrkModule
    {
        public string ModuleName => "TestModule2";

        public IReadOnlyList<Service> GetServices() =>
        [
            new(new TestServiceSpec
            {
                Key = "test_service_2",
                DisplayName = "Test Service 2",
                Description = "Another test service",
                IsInfrastructure = false,
                IsEnabled = _ => true,
                BuildContainerSpecs = (_, _, arrk) =>
                [
                    new()
                    {
                        Image = "linuxserver/test2:latest",
                        Name = arrk.GetContainerName("test2"),
                        Labels = new() { [ContainerLabels.Service] = "test_service_2" },
                        RestartPolicy = RestartPolicyType.UnlessStopped
                    }
                ]
            })
        ];
    }

    class ConfigModule : IArrkModule
    {
        public string ModuleName => "ConfigModule";

        public IReadOnlyList<Service> GetServices() => [];

        public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
            [new ConfigSectionProvider<StorageConfig>("test_section", "Test Section")];
    }
}
