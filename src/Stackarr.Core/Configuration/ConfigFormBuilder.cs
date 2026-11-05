using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Stackarr.Core.Configuration;

/// <summary>
/// Builds a <see cref="ConfigFormModel"/> from the JSON schema produced by
/// <see cref="ConfigSchemaGenerator"/>, seeded with the effective config values.
/// </summary>
/// <remarks>
/// This is the single bridge between "the config as the JSON schema describes it"
/// and "the config as a friendly form". It has no knowledge of any module: every
/// section, label and field comes from the registry and the schema.
/// </remarks>
public class ConfigFormBuilder(ConfigSchemaGenerator schemaGenerator, ConfigSectionRegistry registry)
{
    public ConfigFormModel Build(string effectiveYaml)
    {
        JsonElement schema = schemaGenerator.GenerateSchema();
        if (!schema.TryGetProperty("properties", out JsonElement rootProps))
            return new ConfigFormModel();

        Dictionary<string, object?> values = ParseYaml(effectiveYaml);

        List<ConfigFormSection> sections = [];

        foreach (IConfigSectionProvider provider in registry.GetAll()
                     .Where(p => p.IsUserEditable)
                     .OrderBy(p => p.GroupOrder)
                     .ThenBy(p => p.Group, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => p.Order)
                     .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            string[] segments = provider.SectionPath
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(YamlConfigSerializer.ToCamelCase)
                .ToArray();

            if (segments.Length == 0)
                continue;

            JsonElement sectionSchema = NavigateSchema(rootProps, segments);
            if (sectionSchema.ValueKind != JsonValueKind.Object ||
                !sectionSchema.TryGetProperty("properties", out _))
                continue;

            object? sectionValue = NavigateValue(values, segments);

            ConfigFormSection section = new()
            {
                SectionPath = provider.SectionPath,
                IsService = provider.SectionPath.StartsWith("services:", StringComparison.OrdinalIgnoreCase),
                Icon = provider.Icon,
                Group = provider.Group,
                GroupOrder = provider.GroupOrder,
                Order = provider.Order
            };
            ApplyBase(section, segments[^1], string.Join('.', segments), sectionSchema);
            section.Title = provider.DisplayName;
            section.Children = BuildObjectChildren(string.Join('.', segments), sectionSchema, sectionValue);

            sections.Add(section);
        }

        AddGeneralRootFields(rootProps, values, sections);

        return BuildModel(sections);
    }

    /// <summary>
    /// Add the root-level <see cref="StackarrConfig"/> fields that are not a
    /// registered section (e.g. tmdbApiKey, stackName) so nothing is silently lost.
    /// </summary>
    void AddGeneralRootFields(
        JsonElement rootProps,
        Dictionary<string, object?> values,
        List<ConfigFormSection> sections)
    {
        HashSet<string> handledTopLevelKeys = new(
            registry.GetAll()
                .Select(p => p.SectionPath.Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""),
            StringComparer.OrdinalIgnoreCase);

        List<ConfigFormNode> generalChildren = [];
        foreach (JsonProperty prop in rootProps.EnumerateObject())
        {
            if (handledTopLevelKeys.Contains(prop.Name))
                continue;

            object? value = NavigateValue(values, [prop.Name]);
            generalChildren.Add(BuildNode(prop.Name, prop.Name, prop.Value, value));
        }

        if (generalChildren.Count == 0)
            return;

        ConfigFormSection general = new()
        {
            SectionPath = "",
            Icon = "Settings",
            Group = "Application",
            GroupOrder = 0,
            Order = 0,
            Children = generalChildren
        };
        general.Title = "General";
        general.Description = "Application-level settings.";
        sections.Add(general);
    }

    static ConfigFormModel BuildModel(List<ConfigFormSection> sections)
    {
        ConfigFormModel model = new();

        foreach (IGrouping<string, ConfigFormSection> group in sections
                     .GroupBy(s => s.Group)
                     .OrderBy(g => g.Min(s => s.GroupOrder))
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<ConfigFormSection> ordered = group
                .OrderBy(s => s.Order)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            model.Groups.Add(new ConfigFormGroup
            {
                Title = group.Key,
                Icon = ordered.FirstOrDefault()?.Icon ?? "Tune",
                Sections = ordered
            });
        }

        return model;
    }

    List<ConfigFormNode> BuildObjectChildren(string parentPath, JsonElement schema, object? value)
    {
        List<ConfigFormNode> children = [];

        if (!schema.TryGetProperty("properties", out JsonElement properties))
            return children;

        IDictionary? dict = value as IDictionary;

        foreach (JsonProperty prop in properties.EnumerateObject())
        {
            string childPath = string.IsNullOrEmpty(parentPath) ? prop.Name : $"{parentPath}.{prop.Name}";
            object? childValue = GetDictionaryValue(dict, prop.Name);
            children.Add(BuildNode(prop.Name, childPath, prop.Value, childValue));
        }

        return children;
    }

    ConfigFormNode BuildNode(string key, string path, JsonElement schema, object? value)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            ConfigRawNode raw = new() { Value = DeepCloneValue(value) };
            ApplyBase(raw, key, path, schema);
            raw.FieldType = ConfigFormFieldType.String;
            raw.Type = "string";
            return raw;
        }

        bool hasType = schema.TryGetProperty("type", out JsonElement typeEl);
        string type = hasType ? typeEl.GetString() ?? "string" : "";

        if (type == "object" && schema.TryGetProperty("properties", out _))
            return BuildObject(key, path, schema, value);

        if (type == "object" && schema.TryGetProperty("additionalProperties", out JsonElement additional) &&
            additional.ValueKind == JsonValueKind.Object &&
            (additional.TryGetProperty("type", out _) || additional.TryGetProperty("properties", out _)))
            return BuildDictionary(key, path, schema, additional, value);

        if (type == "array")
            return BuildArray(key, path, schema, value);

        if (!hasType || string.IsNullOrEmpty(type) || type == "object")
        {
            // Untyped/free-form value (e.g. a free-form settings map).
            // Preserve it verbatim so nothing is lost; it is shown read-only
            // with a pointer to the YAML editor.
            return BuildRaw(key, path, schema, value);
        }

        return BuildScalar(key, path, schema, type, value);
    }

    static ConfigRawNode BuildRaw(string key, string path, JsonElement schema, object? value)
    {
        ConfigRawNode raw = new() { Value = DeepCloneValue(value) };
        ApplyBase(raw, key, path, schema);
        raw.FieldType = ConfigFormFieldType.String;
        raw.Type = "string";
        raw.Title = GetTitle(schema, key);
        return raw;
    }

    ConfigObjectNode BuildObject(string key, string path, JsonElement schema, object? value)
    {
        ConfigObjectNode node = new();
        ApplyBase(node, key, path, schema);
        node.FieldType = ConfigFormFieldType.Object;
        node.Children = BuildObjectChildren(path, schema, value);
        return node;
    }

    ConfigArrayNode BuildArray(string key, string path, JsonElement schema, object? value)
    {
        ConfigArrayNode node = new();
        ApplyBase(node, key, path, schema);
        node.FieldType = ConfigFormFieldType.Array;

        JsonElement itemSchema = schema.TryGetProperty("items", out JsonElement items)
            ? items
            : default;

        node.ItemPrototype = BuildNode("", $"{path}[]", itemSchema, null);

        if (value is IList list)
        {
            for (int i = 0; i < list.Count; i++)
                node.Items.Add(BuildNode(i.ToString(CultureInfo.InvariantCulture), $"{path}[{i}]", itemSchema, list[i]));
        }

        return node;
    }

    ConfigDictionaryNode BuildDictionary(
        string key,
        string path,
        JsonElement schema,
        JsonElement valueSchema,
        object? value)
    {
        ConfigDictionaryNode node = new();
        ApplyBase(node, key, path, schema);
        node.FieldType = ConfigFormFieldType.Dictionary;
        node.ValuePrototype = BuildNode("", $"{path}{{}}", valueSchema, null);

        if (value is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                string entryKey = entry.Key?.ToString() ?? "";
                node.Entries.Add(new ConfigDictionaryEntry
                {
                    Key = entryKey,
                    Value = BuildNode(entryKey, $"{path}.{entryKey}", valueSchema, entry.Value)
                });
            }
        }

        return node;
    }

    ConfigScalarNode BuildScalar(string key, string path, JsonElement schema, string type, object? value)
    {
        ConfigScalarNode node = new();
        ApplyBase(node, key, path, schema);
        node.FieldType = type switch
        {
            "boolean" => ConfigFormFieldType.Boolean,
            "integer" => ConfigFormFieldType.Integer,
            "number" => ConfigFormFieldType.Number,
            _ when schema.TryGetProperty("enum", out _) => ConfigFormFieldType.Enum,
            _ => ConfigFormFieldType.String
        };

        object? effective = value;
        if (effective == null && schema.TryGetProperty("default", out JsonElement def))
            effective = JsonElementToObject(def);

        node.Value = NormalizeScalar(effective, node.FieldType);
        return node;
    }

    static void ApplyBase(ConfigFormNode node, string key, string path, JsonElement schema)
    {
        node.Key = key;
        node.Path = path;
        node.Title = GetTitle(schema, key);

        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("description", out JsonElement desc))
                node.Description = desc.GetString() ?? "";

            if (schema.TryGetProperty("format", out JsonElement format))
                node.Format = format.GetString();

            if (schema.TryGetProperty("default", out JsonElement def))
                node.DefaultValue = JsonElementToObject(def);

            if (schema.TryGetProperty("enum", out JsonElement enumEl) && enumEl.ValueKind == JsonValueKind.Array)
                node.EnumValues = enumEl.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(e => e.Length > 0)
                    .ToList();

            if (schema.TryGetProperty("minimum", out JsonElement min)) node.Minimum = min.GetDouble();
            if (schema.TryGetProperty("maximum", out JsonElement max)) node.Maximum = max.GetDouble();
            if (schema.TryGetProperty("exclusiveMinimum", out JsonElement exMin)) node.ExclusiveMinimum = exMin.GetDouble();
            if (schema.TryGetProperty("exclusiveMaximum", out JsonElement exMax)) node.ExclusiveMaximum = exMax.GetDouble();
            if (schema.TryGetProperty("minLength", out JsonElement minLen)) node.MinLength = minLen.GetInt32();
            if (schema.TryGetProperty("maxLength", out JsonElement maxLen)) node.MaxLength = maxLen.GetInt32();

            node.Required = node.MinLength is > 0 || schema.TryGetProperty("required", out _);
        }

        node.FieldType = node.FieldType == ConfigFormFieldType.String && node.EnumValues.Count > 0
            ? ConfigFormFieldType.Enum
            : node.FieldType;
    }

    static string GetTitle(JsonElement schema, string key)
    {
        if (schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("title", out JsonElement title) &&
            !string.IsNullOrEmpty(title.GetString()))
            return title.GetString()!;

        return ConfigSchemaGenerator.Humanize(key);
    }

    static object? NormalizeScalar(object? value, ConfigFormFieldType fieldType)
    {
        if (value == null)
        {
            return fieldType switch
            {
                ConfigFormFieldType.Boolean => false,
                ConfigFormFieldType.Integer => 0L,
                ConfigFormFieldType.Number => 0d,
                _ => null
            };
        }

        switch (fieldType)
        {
            case ConfigFormFieldType.Boolean:
                if (value is bool b) return b;
                return bool.TryParse(value.ToString(), out bool parsedBool) && parsedBool;

            case ConfigFormFieldType.Integer:
                if (value is long l) return l;
                if (value is int i) return (long)i;
                if (value is double d) return (long)d;
                return long.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out long pl) ? pl : 0L;

            case ConfigFormFieldType.Number:
                if (value is double dd) return dd;
                if (value is float f) return (double)f;
                if (value is decimal m) return (double)m;
                if (value is long ll) return (double)ll;
                return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double pd) ? pd : 0d;

            default:
                return value.ToString();
        }
    }

    static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        _ => null
    };

    static JsonElement NavigateSchema(JsonElement rootProperties, string[] segments)
    {
        JsonElement current = rootProperties;

        for (int i = 0; i < segments.Length; i++)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segments[i], out JsonElement child))
                return default;

            current = child;

            if (i < segments.Length - 1)
            {
                if (!current.TryGetProperty("properties", out JsonElement nested))
                    return default;

                current = nested;
            }
        }

        return current;
    }

    static object? NavigateValue(Dictionary<string, object?> values, string[] segments)
    {
        object? current = values;

        foreach (string segment in segments)
        {
            if (current is not IDictionary dict)
                return null;

            current = GetDictionaryValue(dict, segment);
        }

        return current;
    }

    static object? GetDictionaryValue(IDictionary? dict, string key)
    {
        if (dict == null)
            return null;

        foreach (DictionaryEntry entry in dict)
        {
            if (entry.Key?.ToString()?.Equals(key, StringComparison.OrdinalIgnoreCase) == true)
                return entry.Value;
        }

        return null;
    }

    /// <summary>Parse a YAML document into a normalized dictionary graph.</summary>
    public static Dictionary<string, object?> ParseYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            object? parsed = YamlConfigSerializer.Deserialize<object>(yaml);
            return Normalize(parsed) as Dictionary<string, object?> ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Normalize a YamlDotNet object graph into dictionaries/lists/scalars.</summary>
    public static object? Normalize(object? node)
    {
        switch (node)
        {
            case null:
                return null;

            case IDictionary dict:
                Dictionary<string, object?> normalized = new(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in dict)
                    normalized[entry.Key?.ToString() ?? ""] = Normalize(entry.Value);
                return normalized;

            case string:
                return node;

            case IList list:
                return list.Cast<object?>().Select(Normalize).ToList();

            default:
                return node;
        }
    }

    /// <summary>Deep-clone a normalized value graph.</summary>
    public static object? DeepCloneValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case IDictionary dict:
                Dictionary<string, object?> clone = new(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in dict)
                    clone[entry.Key?.ToString() ?? ""] = DeepCloneValue(entry.Value);
                return clone;

            case IList list:
                return list.Cast<object?>().Select(DeepCloneValue).ToList();

            default:
                return value;
        }
    }
}
