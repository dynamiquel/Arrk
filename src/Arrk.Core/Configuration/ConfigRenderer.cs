using Microsoft.Extensions.Configuration;
using Arrk.Core.Configuration;

namespace Arrk.Core.Configuration;

/// <summary>
/// Renders the current IConfiguration as a YAML document using YamlConfigSerializer.
/// </summary>
public static class ConfigRenderer
{
    /// <summary>
    /// Render the full config from IConfiguration as YAML.
    /// </summary>
    public static string Render(IConfiguration configuration, ConfigSectionRegistry registry)
    {
        return YamlConfigSerializer.RenderFullConfig(configuration, registry);
    }
}
