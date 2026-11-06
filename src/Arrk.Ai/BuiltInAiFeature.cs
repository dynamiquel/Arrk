using Microsoft.Extensions.Options;

namespace Arrk.Ai;

/// <summary>
/// Descriptor for a feature whose toggle lives in <see cref="AiConfig.Features"/>. Modules with
/// their own config section can implement <see cref="IAiFeatureDescriptor"/> directly instead.
/// </summary>
public sealed class BuiltInAiFeature(
    IOptionsMonitor<AiConfig> config,
    string key,
    string name,
    string description,
    Func<AiConfig, bool> isEnabled) : IAiFeatureDescriptor
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Description { get; } = description;

    public bool IsEnabled => config.CurrentValue.Enabled && isEnabled(config.CurrentValue);

}
