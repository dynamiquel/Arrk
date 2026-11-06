using Microsoft.Extensions.Configuration;

namespace Arrk.Core.Services;

public class ServiceRegistry
{
    private readonly Dictionary<string, Service> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Service> _orderedServices = [];

    public IReadOnlyCollection<Service> All => _orderedServices.AsReadOnly();

    public void Register(Service service)
    {
        _services[service.Definition.Key] = service;
        _orderedServices.Add(service);
    }

    public Service? Get(string key)
    {
        return _services.TryGetValue(key, out Service? service) ? service : null;
    }

    public Service GetRequired(string key)
    {
        return Get(key) ?? throw new KeyNotFoundException($"Service '{key}' not found in registry");
    }

    public IEnumerable<Service> GetEnabled(IConfiguration configuration)
    {
        return _orderedServices.Where(s => s.Definition.IsEnabled(configuration));
    }

    public IEnumerable<Service> GetInfrastructure()
    {
        return _orderedServices.Where(s => s.Definition.IsInfrastructure);
    }

    public IEnumerable<Service> GetNonInfrastructure()
    {
        return _orderedServices.Where(s => !s.Definition.IsInfrastructure);
    }

    public bool Contains(string key)
    {
        return _services.ContainsKey(key);
    }

    public IReadOnlyList<string> GetEnabledKeys(IConfiguration configuration)
    {
        return GetEnabled(configuration)
            .Select(s => s.Definition.Key)
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<string> GetDisabledKeys(IConfiguration configuration)
    {
        return GetNonInfrastructure()
            .Where(s => !s.Definition.IsEnabled(configuration))
            .Select(s => s.Definition.Key)
            .ToList()
            .AsReadOnly();
    }

    public Service? FindServiceByContainerLabel(string containerServiceLabel)
    {
        foreach (Service service in _orderedServices)
        {
            if (service.Definition.Key.Equals(containerServiceLabel, StringComparison.OrdinalIgnoreCase))
                return service;
        }
        return null;
    }
}
