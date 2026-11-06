using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core;
using Arrk.Core.Services;

namespace Arrk.App;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ArrkDbContext>
{
    public ArrkDbContext CreateDbContext(string[] args)
    {
        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddYamlFile("arrk.yaml", optional: true)
            .Build();

        ServiceCollection services = new();
        ModuleLoader loader = ServiceRegistryFactory.CreateModuleLoader();
        loader.ConfigureAllServices(services, config);

        using ServiceProvider provider = services.BuildServiceProvider();
        IEnumerable<IModuleEntityConfiguration> configurations =
            provider.GetServices<IModuleEntityConfiguration>();

        string dbPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "arrk.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        return new ArrkDbContext(options, configurations);
    }
}
