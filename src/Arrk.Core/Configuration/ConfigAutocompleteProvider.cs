using System.Text.Json;

namespace Arrk.Core.Configuration;

/// <summary>
/// Provides autocomplete suggestions for the YAML config editor.
/// Uses ConfigSchemaGenerator for type info and YamlContextResolver for position awareness.
/// </summary>
public class ConfigAutocompleteProvider(ConfigSchemaGenerator schemaGenerator)
{
    public AutocompleteResult GetSuggestions(string yamlText, int cursorOffset)
    {
        YamlContext context = YamlContextResolver.GetContext(yamlText, cursorOffset);
        JsonElement schema = schemaGenerator.GenerateSchema();

        if (!schema.TryGetProperty("properties", out JsonElement rootProps))
            return new() { Suggestions = [], Context = context };

        List<AutocompleteSuggestion> suggestions;

        if (context.IsKeyContext)
        {
            suggestions = GetKeySuggestions(context, rootProps);
        }
        else if (context.IsAfterColon)
        {
            List<string> navPath = new(context.ParentKeys);
            if (!string.IsNullOrEmpty(context.CurrentKey))
                navPath.Add(context.CurrentKey);

            JsonElement node = NavigateToProperty(rootProps, navPath);
            bool isSection = node.ValueKind == JsonValueKind.Object &&
                             node.TryGetProperty("properties", out _);

            suggestions = isSection
                ? GetKeySuggestions(context, rootProps)
                : GetValueSuggestions(context, rootProps);
        }
        else
        {
            suggestions = GetValueSuggestions(context, rootProps);
        }

        return new() { Suggestions = suggestions, Context = context };
    }

    List<AutocompleteSuggestion> GetKeySuggestions(YamlContext context, JsonElement rootProps)
    {
        JsonElement targetNode;
        if (context.ParentKeys.Count == 0)
        {
            targetNode = rootProps;
        }
        else
        {
            targetNode = NavigateToProperties(rootProps, context.ParentKeys);
        }

        if (targetNode.ValueKind != JsonValueKind.Object)
            return [];

        HashSet<string> usedKeys = YamlContextResolver.GetUsedKeys(
            context.CurrentLineText != "" ? "" : "", context);

        List<AutocompleteSuggestion> suggestions = [];
        foreach (JsonProperty kvp in targetNode.EnumerateObject())
        {
            string key = kvp.Name;
            if (!context.IsAfterColon && usedKeys.Contains(key))
                continue;

            JsonElement propSchema = kvp.Value;
            suggestions.Add(BuildKeySuggestion(key, propSchema));
        }

        return suggestions;
    }

    List<AutocompleteSuggestion> GetValueSuggestions(YamlContext context, JsonElement rootProps)
    {
        List<string> allKeys = new(context.ParentKeys);
        if (!string.IsNullOrEmpty(context.CurrentKey))
            allKeys.Add(context.CurrentKey);

        JsonElement node = NavigateToProperty(rootProps, allKeys);
        if (node.ValueKind != JsonValueKind.Object)
            return [];

        List<AutocompleteSuggestion> suggestions = [];

        if (node.TryGetProperty("enum", out JsonElement enumEl))
        {
            foreach (JsonElement val in enumEl.EnumerateArray())
            {
                string str = val.GetString() ?? "";
                if (!string.IsNullOrEmpty(str))
                {
                    suggestions.Add(new()
                    {
                        Label = str,
                        InsertText = str,
                        Kind = CompletionKind.EnumValue,
                        TypeHint = "enum"
                    });
                }
            }
            return suggestions;
        }

        if (node.TryGetProperty("type", out JsonElement typeEl))
        {
            string? typeName = typeEl.GetString();
            switch (typeName)
            {
                case "boolean":
                    suggestions.Add(new()
                    {
                        Label = "true", InsertText = "true",
                        Kind = CompletionKind.Value, TypeHint = "boolean"
                    });
                    suggestions.Add(new()
                    {
                        Label = "false", InsertText = "false",
                        Kind = CompletionKind.Value, TypeHint = "boolean"
                    });
                    break;

                case "string":
                    suggestions.Add(new()
                    {
                        Label = "\"\"", InsertText = "\"\"",
                        Kind = CompletionKind.Value, TypeHint = "string",
                        Description = GetStringDescription(node)
                    });
                    break;

                case "integer":
                    suggestions.Add(new()
                    {
                        Label = "0", InsertText = "0",
                        Kind = CompletionKind.Value, TypeHint = "integer",
                        Description = GetNumericDescription(node)
                    });
                    break;

                case "number":
                    suggestions.Add(new()
                    {
                        Label = "0.0", InsertText = "0",
                        Kind = CompletionKind.Value, TypeHint = "number",
                        Description = GetNumericDescription(node)
                    });
                    break;

                case "array":
                    suggestions.Add(new()
                    {
                        Label = "- ", InsertText = "- ",
                        Kind = CompletionKind.Value, TypeHint = "array item",
                        Description = "add list item"
                    });
                    break;
            }
        }

        return suggestions;
    }

    JsonElement NavigateToProperties(JsonElement rootProps, List<string> path)
    {
        JsonElement current = rootProps;
        foreach (string key in path)
        {
            if (current.TryGetProperty(key, out JsonElement child))
            {
                if (child.TryGetProperty("properties", out JsonElement props))
                    current = props;
                else
                    return child;
            }
            else
            {
                return default;
            }
        }
        return current;
    }

    JsonElement NavigateToProperty(JsonElement rootProps, List<string> path)
    {
        JsonElement current = rootProps;
        for (int i = 0; i < path.Count; i++)
        {
            string key = path[i];
            if (current.TryGetProperty(key, out JsonElement child))
            {
                if (i < path.Count - 1 && child.TryGetProperty("properties", out JsonElement props))
                    current = props;
                else
                    return child;
            }
            else
            {
                return default;
            }
        }
        return current;
    }

    static AutocompleteSuggestion BuildKeySuggestion(string key, JsonElement propSchema)
    {
        AutocompleteSuggestion suggestion = new()
        {
            Label = key,
            InsertText = key + ": ",
            Kind = CompletionKind.Property
        };

        if (propSchema.TryGetProperty("type", out JsonElement typeEl))
            suggestion.TypeHint = typeEl.GetString() ?? "";

        if (propSchema.TryGetProperty("description", out JsonElement descEl))
            suggestion.Description = descEl.GetString() ?? "";

        if (propSchema.TryGetProperty("default", out JsonElement defaultEl) && defaultEl.ValueKind != JsonValueKind.Null)
            suggestion.DefaultValue = FormatJsonValue(defaultEl);

        if (suggestion.TypeHint == "object")
        {
            suggestion.Description = string.IsNullOrEmpty(suggestion.Description)
                ? "section"
                : suggestion.Description + " (section)";
            suggestion.InsertText = key + ":\n  ";
        }

        return suggestion;
    }

    static string GetStringDescription(JsonElement schema)
    {
        List<string> parts = [];
        if (schema.TryGetProperty("minLength", out JsonElement min))
            parts.Add($"min length {min.GetInt32()}");
        if (schema.TryGetProperty("maxLength", out JsonElement max))
            parts.Add($"max length {max.GetInt32()}");
        return parts.Count > 0 ? string.Join(", ", parts) : "string value";
    }

    static string GetNumericDescription(JsonElement schema)
    {
        List<string> parts = new();
        if (schema.TryGetProperty("minimum", out JsonElement min))
            parts.Add($">= {FormatJsonValue(min)}");
        if (schema.TryGetProperty("exclusiveMinimum", out JsonElement exMin))
            parts.Add($"> {FormatJsonValue(exMin)}");
        if (schema.TryGetProperty("maximum", out JsonElement max))
            parts.Add($"<= {FormatJsonValue(max)}");
        if (schema.TryGetProperty("exclusiveMaximum", out JsonElement exMax))
            parts.Add($"< {FormatJsonValue(exMax)}");
        return parts.Count > 0 ? string.Join(", ", parts) : "number";
    }

    static string FormatJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => $"\"{element.GetString()}\"",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        _ => element.GetRawText()
    };
}

public class AutocompleteResult
{
    public List<AutocompleteSuggestion> Suggestions { get; set; } = new();
    public YamlContext Context { get; set; } = new();
}

public class AutocompleteSuggestion
{
    public string Label { get; set; } = "";
    public string InsertText { get; set; } = "";
    public string TypeHint { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public string Description { get; set; } = "";
    public CompletionKind Kind { get; set; }
}

public enum CompletionKind
{
    Property,
    Value,
    EnumValue
}
