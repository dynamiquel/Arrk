namespace Stackarr.Core.Configuration;

/// <summary>
/// Strongly-typed implementation of IConfigSectionProvider.
/// Instantiates defaults via new TConfig() (uses POCO property initializers).
/// </summary>
public class ConfigSectionProvider<TConfig>(
    string sectionPath,
    string displayName,
    bool isUserEditable = true,
    string icon = "Tune",
    string group = "General",
    int order = 0,
    int groupOrder = 100)
    : IConfigSectionProvider
    where TConfig : new()
{
    public string SectionPath { get; } = sectionPath;
    public string DisplayName { get; } = displayName;
    public Type ConfigType { get; } = typeof(TConfig);
    public bool IsUserEditable { get; } = isUserEditable;
    public string Icon { get; } = icon;
    public string Group { get; } = group;
    public int Order { get; } = order;
    public int GroupOrder { get; } = groupOrder;

    public TConfig GetDefaults() => new();
}
