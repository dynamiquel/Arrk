using System.Collections;
using System.Globalization;

namespace Arrk.Core.Configuration;

/// <summary>
/// Serializes a <see cref="ConfigFormModel"/> back into the same YAML shape the
/// app consumes. When the default config is supplied, values that still match
/// their default are omitted so the saved file only contains what was actually
/// changed. Values for unknown/unmanaged keys from the original document can be
/// preserved so the visual editor never silently drops user data.
/// </summary>
public static class ConfigFormSerializer
{
    public static string Serialize(
        ConfigFormModel model,
        string? preserveFromYaml = null,
        string? defaultsYaml = null)
    {
        Dictionary<string, object?>? defaults = string.IsNullOrWhiteSpace(defaultsYaml)
            ? null
            : ConfigFormBuilder.ParseYaml(defaultsYaml);

        Dictionary<string, object?> graph = BuildGraph(model, defaults);

        if (!string.IsNullOrWhiteSpace(preserveFromYaml))
            MergeUnknownKeys(graph, ConfigFormBuilder.ParseYaml(preserveFromYaml));

        return YamlConfigSerializer.Serialize(graph);
    }

    /// <summary>
    /// Full, un-diffed object graph for the form (used by tests/tools).
    /// </summary>
    public static Dictionary<string, object?> ToGraph(ConfigFormModel model) =>
        BuildGraph(model, defaults: null);

    static Dictionary<string, object?> BuildGraph(ConfigFormModel model, Dictionary<string, object?>? defaults)
    {
        Dictionary<string, object?> root = new(StringComparer.OrdinalIgnoreCase);

        foreach (ConfigFormSection section in model.TopLevelSections)
        {
            string[] segments = section.SectionPath
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(YamlConfigSerializer.ToCamelCase)
                .ToArray();

            object? sectionDefault = defaults == null ? null : GetByPath(defaults, segments);

            if (segments.Length == 0)
            {
                // General root fields: diff each one individually.
                foreach (ConfigFormNode child in section.Children)
                {
                    object? childDefault = defaults == null ? null : GetDictionaryValue(defaults, child.Key);
                    (bool include, object? value) = SerializeNode(child, childDefault, defaults == null);
                    if (include)
                        root[child.Key] = value;
                }
                continue;
            }

            (bool includeSection, object? sectionValue) = SerializeNode(section, sectionDefault, defaults == null);
            if (includeSection)
                SetNested(root, segments, sectionValue);
        }

        return root;
    }

    /// <summary>
    /// Serialize a node, optionally omitting it when it equals its default.
    /// Returns whether the node should be included and its value.
    /// </summary>
    static (bool Include, object? Value) SerializeNode(
        ConfigFormNode node,
        object? defaultNode,
        bool includeAll)
    {
        switch (node)
        {
            case ConfigScalarNode scalar:
            {
                object? scalarDefault = defaultNode ?? scalar.DefaultValue;
                return includeAll || !ValuesEqual(scalar.Value, scalarDefault)
                    ? (true, scalar.Value)
                    : (false, null);
            }

            case ConfigRawNode raw:
            {
                object? rawDefault = defaultNode ?? raw.DefaultValue;
                return includeAll || !ValuesEqual(raw.Value, rawDefault)
                    ? (true, raw.Value)
                    : (false, null);
            }

            case ConfigArrayNode array:
            {
                object value = array.Items.Select(i => SerializeNode(i, null, true).Value).Where(v => v != null).ToList();
                return includeAll || !ValuesEqual(value, defaultNode)
                    ? (true, value)
                    : (false, null);
            }

            case ConfigDictionaryNode dictionary:
            {
                Dictionary<string, object?> value = SerializeDictionary(dictionary);
                return includeAll || !ValuesEqual(value, defaultNode)
                    ? (true, value)
                    : (false, null);
            }

            case ConfigObjectNode obj:
            {
                IDictionary? defaultDict = defaultNode as IDictionary;
                Dictionary<string, object?> value = new(StringComparer.OrdinalIgnoreCase);

                foreach (ConfigFormNode child in obj.Children)
                {
                    object? childDefault = defaultDict == null ? null : GetDictionaryValue(defaultDict, child.Key);
                    (bool include, object? childValue) = SerializeNode(child, childDefault, includeAll);
                    if (include)
                        value[child.Key] = childValue;
                }

                return value.Count > 0 ? (true, value) : (false, null);
            }

            default:
                return (false, null);
        }
    }

    static Dictionary<string, object?> SerializeDictionary(ConfigDictionaryNode node)
    {
        Dictionary<string, object?> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (ConfigDictionaryEntry entry in node.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;

            result[entry.Key] = SerializeNode(entry.Value, null, true).Value;
        }

        return result;
    }

    static object? GetByPath(Dictionary<string, object?> root, string[] segments)
    {
        object? current = root;

        foreach (string segment in segments)
        {
            if (current is not IDictionary dict)
                return null;

            current = GetDictionaryValue(dict, segment);
        }

        return current;
    }

    static object? GetDictionaryValue(IDictionary dict, string key)
    {
        foreach (DictionaryEntry entry in dict)
        {
            if (entry.Key?.ToString()?.Equals(key, StringComparison.OrdinalIgnoreCase) == true)
                return entry.Value;
        }

        return null;
    }

    /// <summary>
    /// Deep, type-tolerant equality used to decide whether a value still matches
    /// its default. Maps and lists are compared as a whole so removals are kept.
    /// Scalar comparison tolerates the fact that YAML scalars may arrive as
    /// strings (e.g. "true" / "2.0") while the form holds typed values.
    /// </summary>
    internal static bool ValuesEqual(object? a, object? b)
    {
        if (a is null || b is null)
            return a is null && b is null;

        if (a is IDictionary da && b is IDictionary db)
        {
            if (da.Count != db.Count)
                return false;

            foreach (DictionaryEntry entry in da)
            {
                string key = entry.Key?.ToString() ?? "";
                object? other = GetDictionaryValue(db, key);
                if (!ValuesEqual(entry.Value, other))
                    return false;
            }
            return true;
        }

        if (a is IList la && b is IList lb)
        {
            if (la.Count != lb.Count)
                return false;

            for (int i = 0; i < la.Count; i++)
            {
                if (!ValuesEqual(la[i], lb[i]))
                    return false;
            }
            return true;
        }

        if (TryBool(a, out bool ba) && TryBool(b, out bool bb))
            return ba == bb;

        if (TryDecimal(a, out decimal na) && TryDecimal(b, out decimal nb))
            return na == nb;

        return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    static bool TryBool(object value, out bool result)
    {
        if (value is bool b)
        {
            result = b;
            return true;
        }

        if (value is string s && bool.TryParse(s, out bool parsed))
        {
            result = parsed;
            return true;
        }

        result = false;
        return false;
    }

    static bool TryDecimal(object value, out decimal result)
    {
        if (IsNumeric(value))
        {
            result = Convert.ToDecimal(value);
            return true;
        }

        if (value is string s && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed))
        {
            result = parsed;
            return true;
        }

        result = 0m;
        return false;
    }

    static bool IsNumeric(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    static void SetNested(Dictionary<string, object?> root, string[] segments, object? value)
    {
        Dictionary<string, object?> current = root;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            string key = segments[i];
            if (!current.TryGetValue(key, out object? existing) || existing is not Dictionary<string, object?> nested)
            {
                nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                current[key] = nested;
            }

            current = nested;
        }

        current[segments[^1]] = value;
    }

    static void MergeUnknownKeys(Dictionary<string, object?> graph, Dictionary<string, object?> original)
    {
        foreach (KeyValuePair<string, object?> entry in original)
        {
            if (!graph.TryGetValue(entry.Key, out object? existing))
            {
                graph[entry.Key] = entry.Value;
                continue;
            }

            if (existing is Dictionary<string, object?> graphDict && entry.Value is IDictionary originalDict)
                MergeUnknownKeys(graphDict, ToDictionary(originalDict));
        }
    }

    static Dictionary<string, object?> ToDictionary(IDictionary dict)
    {
        Dictionary<string, object?> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in dict)
            result[entry.Key?.ToString() ?? ""] = entry.Value;
        return result;
    }
}
