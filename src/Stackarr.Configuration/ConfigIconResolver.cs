using System.Reflection;
using MudBlazor;

namespace Stackarr.Configuration;

/// <summary>
/// Resolves an icon name supplied by a config section owner (a module) into a
/// MudBlazor icon path. Modules only provide a name, so they never need a
/// dependency on MudBlazor, and the UI never needs module-specific knowledge.
/// </summary>
public static class ConfigIconResolver
{
    static readonly Dictionary<string, string> Index = BuildIndex();

    public static string Resolve(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && Index.TryGetValue(name, out string? icon))
            return icon;

        return Index.TryGetValue("Tune", out string? fallback) ? fallback : "";
    }

    static Dictionary<string, string> BuildIndex()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);

        foreach (FieldInfo field in typeof(Icons.Material.Filled).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(string))
                continue;

            object? value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
            if (value is string icon)
                map[field.Name] = icon;
        }

        return map;
    }
}
