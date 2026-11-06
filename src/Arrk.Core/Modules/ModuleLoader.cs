using System.Reflection;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Core.Services;

namespace Arrk.App;

public class ModuleLoader(ILogger<ModuleLoader> logger)
{
    readonly List<IArrkModule> _modules = [];

    public void RegisterModule(IArrkModule module)
    {
        _modules.Add(module);
        logger.LogInformation("Registered module: {Module}", module.ModuleName);
    }

    public void ScanAssembly(Assembly assembly)
    {
        IEnumerable<Type> moduleTypes = assembly.GetTypes()
            .Where(t => typeof(IArrkModule).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false });

        foreach (Type type in moduleTypes)
        {
            if (Activator.CreateInstance(type) is IArrkModule module)
            {
                RegisterModule(module);
            }
        }
    }

    public void ScanAllAssemblies()
    {
        IEnumerable<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !a.GetName().Name!.StartsWith("System"));

        foreach (Assembly assembly in assemblies)
        {
            ScanAssembly(assembly);
        }
    }

    /// <summary>
    /// Loads drop-in plugin assemblies from <paramref name="directory"/> (recursively) into the
    /// default load context and registers any <see cref="IArrkModule"/> they contain. Called
    /// before the service container is built, so a plugin contributes DI, jobs, endpoints and UI
    /// exactly like a built-in module.
    /// <para>
    /// Assemblies whose simple name is already loaded (the host's own references, or a plugin's copy
    /// of a shared contract) are skipped so a plugin can never shadow <c>Arrk.Core</c>/
    /// <c>Arrk.Ui</c> with a second, type-incompatible copy. An unresolved plugin dependency is
    /// looked up beside the plugin DLL.
    /// </para>
    /// </summary>
    public IReadOnlyList<Assembly> LoadPlugins(string directory)
    {
        List<Assembly> loaded = [];
        if (!System.IO.Directory.Exists(directory))
        {
            return loaded;
        }

        Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName name)
        {
            string candidate = System.IO.Path.Combine(directory, name.Name + ".dll");
            return System.IO.File.Exists(candidate)
                ? context.LoadFromAssemblyPath(System.IO.Path.GetFullPath(candidate))
                : null;
        }

        AssemblyLoadContext.Default.Resolving += ResolveDependency;

        try
        {
            foreach (string dll in System.IO.Directory
                         .EnumerateFiles(directory, "*.dll", System.IO.SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string simpleName = System.IO.Path.GetFileNameWithoutExtension(dll);
                if (AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
                        System.IO.Path.GetFullPath(dll));
                    loaded.Add(assembly);
                    logger.LogInformation("Loaded plugin assembly: {Assembly}", assembly.GetName().Name);
                }
                catch (BadImageFormatException)
                {
                    // A native library beside the plugin (e.g. a media tool); not managed code.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to load plugin assembly {Path}", dll);
                }
            }
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= ResolveDependency;
        }

        foreach (Assembly assembly in loaded)
        {
            ScanAssembly(assembly);
        }

        if (loaded.Count > 0)
        {
            logger.LogInformation("Loaded {Count} plugin assembly(ies) from {Directory}", loaded.Count, directory);
        }

        return loaded;
    }

    public ServiceRegistry BuildRegistry()
    {
        ServiceRegistry registry = new();

        List<Service> allServices = _modules
            .SelectMany(m => m.GetServices())
            .ToList();

        List<Service> ordered = TopologicalSort(allServices);

        foreach (Service service in ordered)
        {
            registry.Register(service);
        }

        logger.LogInformation("Built service registry with {Count} services from {ModuleCount} modules",
            allServices.Count, _modules.Count);

        return registry;
    }

    public void RegisterConfigSections(ConfigSectionRegistry sectionRegistry)
    {
        foreach (IArrkModule module in _modules)
        {
            foreach (IConfigSectionProvider section in module.GetConfigSections())
            {
                sectionRegistry.Register(section);
                logger.LogDebug("Registered config section: {Section} ({DisplayName}) from {Module}",
                    section.SectionPath, section.DisplayName, module.ModuleName);
            }
        }

        logger.LogInformation("Registered {Count} config sections from {ModuleCount} modules",
            _modules.Sum(m => m.GetConfigSections().Count), _modules.Count);
    }

    List<Service> TopologicalSort(List<Service> services)
    {
        Dictionary<string, Service> byKey = services.ToDictionary(s => s.Definition.Key, s => s);
        Dictionary<string, int> inDegree = services.ToDictionary(s => s.Definition.Key, _ => 0);
        Dictionary<string, List<string>> dependents = services.ToDictionary(
            s => s.Definition.Key, _ => new List<string>());

        foreach (Service service in services)
        {
            foreach (string after in service.Definition.ProvisionAfter)
            {
                if (!byKey.ContainsKey(after))
                {
                    continue;
                }

                inDegree[service.Definition.Key]++;
                dependents[after].Add(service.Definition.Key);
            }

            foreach (string before in service.Definition.ProvisionBefore)
            {
                if (!byKey.ContainsKey(before))
                {
                    continue;
                }

                inDegree[before]++;
                dependents[service.Definition.Key].Add(before);
            }
        }

        Queue<string> queue = new(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        List<Service> result = [];

        while (queue.Count > 0)
        {
            string key = queue.Dequeue();
            result.Add(byKey[key]);

            foreach (string dependent in dependents[key])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (result.Count != services.Count)
        {
            IEnumerable<string> remaining = services.Where(s => !result.Contains(s)).Select(s => s.Definition.Key);
            logger.LogWarning("Circular dependency detected among: {Services}. Appending in registration order.",
                string.Join(", ", remaining));
            result.AddRange(services.Where(s => !result.Contains(s)));
        }

        return result;
    }

    public void ConfigureAllServices(IServiceCollection services, IConfiguration configuration)
    {
        foreach (IArrkModule module in _modules)
        {
            logger.LogDebug("Configuring services for module: {Module}", module.ModuleName);
            module.ConfigureServices(services, configuration);
        }
    }

    public void ConfigureAllHostedServices(IServiceCollection services)
    {
        foreach (IArrkModule module in _modules)
        {
            logger.LogDebug("Configuring hosted services for module: {Module}", module.ModuleName);
            module.ConfigureHostedServices(services);
        }
    }

    public void ConfigureAllJobs(IServiceProvider serviceProvider)
    {
        IArrkJobProvider jobProvider = serviceProvider.GetRequiredService<IArrkJobProvider>();
        foreach (IArrkModule module in _modules)
        {
            logger.LogDebug("Configuring jobs for module: {Module}", module.ModuleName);
            module.ConfigureJobs(jobProvider, serviceProvider);
        }
    }

    public void ConfigureAllEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (IArrkModule module in _modules)
        {
            logger.LogDebug("Configuring endpoints for module: {Module}", module.ModuleName);
            module.ConfigureEndpoints(endpoints);
        }
    }

    public IReadOnlyList<IArrkModule> Modules => _modules.AsReadOnly();

    public IReadOnlyList<ModuleInfo> GetModules()
    {
        return _modules
            .Select(m => new ModuleInfo(
                m.ModuleName,
                m.ModuleVersion,
                m.ModuleAuthor,
                m.ModuleDescription,
                m.GetDependencies()))
            .ToList();
    }
}
