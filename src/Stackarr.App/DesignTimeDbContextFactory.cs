using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core;
using Stackarr.Core.Services;

namespace Stackarr.App;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<StackarrDbContext>
{
    public StackarrDbContext CreateDbContext(string[] args)
    {
        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddYamlFile("stackarr.yaml", optional: true)
            .Build();

        ServiceCollection services = new();
        ModuleLoader loader = ServiceRegistryFactory.CreateModuleLoader();
        loader.ConfigureAllServices(services, config);

        using ServiceProvider provider = services.BuildServiceProvider();
        IEnumerable<IModuleEntityConfiguration> configurations =
            provider.GetServices<IModuleEntityConfiguration>();

        string dbPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "stackarr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        return new StackarrDbContext(options, configurations);
    }
}
