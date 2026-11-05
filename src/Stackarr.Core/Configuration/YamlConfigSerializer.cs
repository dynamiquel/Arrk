using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Stackarr.Core.Configuration;

/// <summary>
/// Serializes/deserializes .NET objects to/from YAML with camelCase naming.
/// Used by ConfigRenderer and validation pipeline.
/// </summary>
public static class YamlConfigSerializer
{
    static readonly ISerializer Serializer;
    static readonly IDeserializer Deserializer;

    static YamlConfigSerializer()
    {
        Serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .DisableAliases()
            .Build();

        Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithCaseInsensitivePropertyMatching()
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Serialize any object to YAML.
    /// </summary>
    public static string Serialize(object obj)
    {
        if (obj == null)
            return string.Empty;

        StringBuilder sb = new();
        using StringWriter writer = new(sb);
        Serializer.Serialize(writer, obj);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Deserialize YAML to a strongly-typed object.
    /// </summary>
    public static T? Deserialize<T>(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return default;

        return Deserializer.Deserialize<T>(yaml);
    }

    /// <summary>
    /// Deserialize YAML to an object of the given type.
    /// </summary>
    public static object? Deserialize(string yaml, Type type)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return null;

        return Deserializer.Deserialize(yaml, type);
    }

    /// <summary>
    /// Render a full config from registered sections as a single YAML document.
    /// Always shows ALL registered sections with their defaults, merging in
    /// any values from IConfiguration.
    /// </summary>
    public static string RenderFullConfig(IConfiguration configuration, ConfigSectionRegistry registry)
    {
        Dictionary<string, object?> root = new(StringComparer.OrdinalIgnoreCase);

        foreach (IConfigSectionProvider provider in registry.GetAll()
                     .OrderBy(p => p.SectionPath, StringComparer.OrdinalIgnoreCase))
        {
            IConfigurationSection section = configuration.GetSection(provider.SectionPath);
            object? obj = GetSectionOrDefault(section, provider.ConfigType);
            string[] segments = provider.SectionPath
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(ToCamelCase)
                .ToArray();
            if (segments.Length == 0)
                continue;

            SetNested(root, segments, ToPlainObject(obj));
        }

        return root.Count == 0 ? string.Empty : Serialize(root);
    }

    static readonly JsonSerializerOptions PlainObjectOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    static object? ToPlainObject(object? value)
    {
        if (value is null)
            return null;

        string json = JsonSerializer.Serialize(value, PlainObjectOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        return ToPlainObject(document.RootElement);
    }

    static object? ToPlainObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => ToPlainObject(property.Value),
                StringComparer.OrdinalIgnoreCase),
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlainObject).ToList(),
        JsonValueKind.Number => element.TryGetInt64(out long integer) ? integer : element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    static void SetNested(
        Dictionary<string, object?> root,
        IReadOnlyList<string> segments,
        object? value)
    {
        Dictionary<string, object?> current = root;
        for (int index = 0; index < segments.Count - 1; index++)
        {
            string key = segments[index];
            if (!current.TryGetValue(key, out object? existing) ||
                existing is not Dictionary<string, object?> nested)
            {
                nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                current[key] = nested;
            }

            current = nested;
        }

        string last = segments[^1];
        if (value is Dictionary<string, object?> valueDictionary &&
            current.TryGetValue(last, out object? currentValue) &&
            currentValue is Dictionary<string, object?> currentDictionary)
        {
            Merge(currentDictionary, valueDictionary);
        }
        else
        {
            current[last] = value;
        }
    }

    static void Merge(Dictionary<string, object?> target, Dictionary<string, object?> source)
    {
        foreach ((string key, object? value) in source)
        {
            if (value is Dictionary<string, object?> sourceNested &&
                target.TryGetValue(key, out object? existing) &&
                existing is Dictionary<string, object?> targetNested)
            {
                Merge(targetNested, sourceNested);
            }
            else
            {
                target[key] = value;
            }
        }
    }

    /// <summary>
    /// Create a default POCO instance, then overlay IConfiguration values if the section exists.
    /// Always returns a non-null object.
    /// </summary>
    static object GetSectionOrDefault(IConfigurationSection section, Type configType)
    {
        object obj = Activator.CreateInstance(configType)!;

        if (section.Exists())
        {
            try
            {
                // Replace list values rather than appending to the POCO's defaults (the standard
                // binder appends, which duplicated qualityProfiles/libraries on every save).
                ConfigOverlay.Apply(section, obj);
            }
            catch
            {
                // Bind failed, stick with defaults
            }
        }

        return obj;
    }

    /// <summary>
    /// Converts a property or section name to camelCase.
    /// Handles both PascalCase (MaxActiveDownloads) and snake_case (max_active_downloads) input.
    /// </summary>
    public static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        StringBuilder sb = new(name.Length);
        bool upperNext = false;

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];

            if (c == '_')
            {
                upperNext = true;
                continue;
            }

            if (sb.Length == 0)
                sb.Append(char.ToLowerInvariant(c));
            else if (upperNext)
                sb.Append(char.ToUpperInvariant(c));
            else
                sb.Append(c);

            upperNext = false;
        }

        return sb.ToString();
    }
}
