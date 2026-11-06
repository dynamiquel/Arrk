namespace Arrk.Core.Configuration;

/// <summary>
/// Registry of the config sections the app knows about, populated at startup by Core and modules
/// registering <see cref="IConfigSectionProvider"/> instances.
/// </summary>
public class ConfigSectionRegistry
{
    readonly List<IConfigSectionProvider> _providers = new();

    public void Register(IConfigSectionProvider provider)
    {
        _providers.Add(provider);
    }

    public IReadOnlyList<IConfigSectionProvider> GetAll() => _providers.AsReadOnly();

    public IReadOnlyList<IConfigSectionProvider> GetUserEditable() =>
        _providers.Where(p => p.IsUserEditable).ToList().AsReadOnly();

    public IConfigSectionProvider? FindByPath(string sectionPath) =>
        _providers.FirstOrDefault(p => p.SectionPath.Equals(sectionPath, StringComparison.OrdinalIgnoreCase));

    public IConfigSectionProvider? FindByType(Type configType) =>
        _providers.FirstOrDefault(p => p.ConfigType == configType);

    public IReadOnlyList<IConfigSectionProvider> GetServiceSections() =>
        _providers.Where(p => p.SectionPath.StartsWith("services:", StringComparison.OrdinalIgnoreCase))
            .ToList().AsReadOnly();

    public IReadOnlyList<IConfigSectionProvider> GetCoreSections() =>
        _providers.Where(p => !p.SectionPath.StartsWith("services:", StringComparison.OrdinalIgnoreCase))
            .ToList().AsReadOnly();
}
