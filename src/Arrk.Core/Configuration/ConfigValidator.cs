using System.Collections;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;

namespace Arrk.Core.Configuration;

/// <summary>
/// Validates YAML config content against JSON Schema (for live editing)
/// and FluentValidation rules (for apply-time cross-field validation).
/// </summary>
public class ConfigValidator(
    ConfigSchemaGenerator schemaGenerator,
    ConfigSectionRegistry registry,
    IServiceProvider serviceProvider)
{
    /// <summary>
    /// Validate YAML syntax by attempting to deserialize it.
    /// Returns the error message if parsing fails.
    /// </summary>
    public string? CheckYamlSyntax(string yamlContent)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
            return null;

        try
        {
            YamlConfigSerializer.Deserialize<Dictionary<string, object>>(yamlContent);
            return null;
        }
        catch (Exception ex)
        {
            return $"YAML syntax error: {ex.Message}";
        }
    }

    /// <summary>
    /// Validate the YAML content against all registered FluentValidation rules.
    /// Uses YamlDotNet deserialization directly (no IConfiguration dependency).
    /// Called on apply/save.
    /// </summary>
    public ConfigValidationResult ValidateWithFluentValidation(string yamlContent)
    {
        List<ConfigValidationError> errors = new();

        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            errors.Add(new() { Path = "", Message = "Configuration is empty" });
            return BuildResult(errors);
        }

        string? syntaxError = CheckYamlSyntax(yamlContent);
        if (syntaxError != null)
        {
            errors.Add(new() { Path = "", Message = syntaxError });
            return BuildResult(errors);
        }

        try
        {
            Dictionary<string, object?>? root = YamlConfigSerializer.Deserialize<Dictionary<string, object?>>(yamlContent);
            if (root == null)
            {
                errors.Add(new() { Path = "", Message = "Failed to parse configuration" });
                return BuildResult(errors);
            }

            foreach (IConfigSectionProvider provider in registry.GetAll())
            {
                object? sectionValue = GetSectionValue(root, provider.SectionPath);
                if (sectionValue == null)
                    continue;

                object? obj = DeserializeSectionValue(sectionValue, provider.ConfigType);
                if (obj == null)
                    continue;

                Type validatorType = typeof(IValidator<>).MakeGenericType(provider.ConfigType);
                IValidator? validator = serviceProvider.GetService(validatorType) as IValidator;
                if (validator == null)
                    continue;

                Type validationContextType = typeof(ValidationContext<>).MakeGenericType(provider.ConfigType);
                IValidationContext validationContext = (IValidationContext)Activator.CreateInstance(validationContextType, obj)!;
                ValidationResult result = validator.Validate(validationContext);

                if (!result.IsValid)
                {
                    foreach (ValidationFailure error in result.Errors)
                    {
                        string propertyName = error.PropertyName ?? "";
                        errors.Add(new()
                        {
                            Path = CombineSectionPath(provider.SectionPath, propertyName),
                            Message = $"{provider.DisplayName}.{propertyName}: {error.ErrorMessage}"
                        });
                    }
                }
            }

            ValidateVpnRouting(root, errors);
        }
        catch (Exception ex)
        {
            errors.Add(new() { Path = "", Message = $"Validation error: {ex.Message}" });
        }

        return BuildResult(errors);
    }

    /// <summary>
    /// Validate YAML content against the generated JSON Schema.
    /// Returns formatted error messages. Used for live editor validation.
    /// </summary>
    public List<string> ValidateWithSchema(string yamlContent) =>
        ValidateWithSchemaDetailed(yamlContent).Select(e => e.Format()).ToList();

    /// <summary>
    /// Validate YAML content against the generated JSON Schema, returning
    /// structured errors whose <see cref="ConfigValidationError.Path"/> can be
    /// mapped back to individual form fields.
    /// </summary>
    public List<ConfigValidationError> ValidateWithSchemaDetailed(string yamlContent)
    {
        List<ConfigValidationError> errors = new();

        if (string.IsNullOrWhiteSpace(yamlContent))
            return errors;

        string? syntaxError = CheckYamlSyntax(yamlContent);
        if (syntaxError != null)
        {
            errors.Add(new() { Path = "", Message = syntaxError });
            return errors;
        }

        try
        {
            Dictionary<string, object?>? root = YamlConfigSerializer.Deserialize<Dictionary<string, object?>>(yamlContent);
            if (root == null)
                return errors;

            JsonElement schema = schemaGenerator.GenerateSchema();
            if (!schema.TryGetProperty("properties", out JsonElement rootProps))
                return errors;

            ValidateObject(root, rootProps, "", errors);
        }
        catch (Exception ex)
        {
            errors.Add(new() { Path = "", Message = $"Schema validation error: {ex.Message}" });
        }

        return errors;
    }

    static ConfigValidationResult BuildResult(List<ConfigValidationError> errors) => new()
    {
        IsValid = errors.Count == 0,
        Errors = errors.Select(e => e.Format()).ToList(),
        Details = errors
    };

    /// <summary>
    /// Navigate a parsed YAML root by a full section path such as "services:tv"
    /// or "routes:cloudflared", camelCasing each segment.
    /// </summary>
    static object? GetSectionValue(IDictionary root, string sectionPath)
    {
        object? current = root;

        foreach (string rawSegment in sectionPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not IDictionary dict)
                return null;

            string segment = YamlConfigSerializer.ToCamelCase(rawSegment);
            object? next = null;
            bool found = false;

            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key?.ToString()?.Equals(segment, StringComparison.OrdinalIgnoreCase) == true)
                {
                    next = entry.Value;
                    found = true;
                    break;
                }
            }

            if (!found)
                return null;

            current = next;
        }

        return current;
    }

    static string CombineSectionPath(string sectionPath, string propertyName)
    {
        string section = string.Join('.', sectionPath
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(YamlConfigSerializer.ToCamelCase));

        if (string.IsNullOrEmpty(propertyName))
            return section;

        string property = string.Join('.', propertyName
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(YamlConfigSerializer.ToCamelCase));

        return string.IsNullOrEmpty(section) ? property : $"{section}.{property}";
    }

    static object? DeserializeSectionValue(object? value, Type targetType)
    {
        if (value == null)
            return null;

        try
        {
            string yaml = YamlConfigSerializer.Serialize(value);
            return YamlConfigSerializer.Deserialize(yaml, targetType);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Cross-section check: opting the torrent downloader into the VPN provisions the tunnel,
    /// so the VPN section must carry usable credentials even when <c>vpn.enabled</c> is false.
    /// </summary>
    static void ValidateVpnRouting(Dictionary<string, object?> root, List<ConfigValidationError> errors)
    {
        object? torrentSection = GetSectionValue(root, "services:torrentDownloader");
        if (torrentSection is not IDictionary torrentDict || !TryGetBool(torrentDict, "vpn"))
            return;

        VpnConfig vpn = DeserializeSectionValue(GetSectionValue(root, "vpn"), typeof(VpnConfig)) as VpnConfig ?? new VpnConfig();

        if (string.IsNullOrWhiteSpace(vpn.Provider))
            errors.Add(new() { Path = "vpn.provider", Message = "VPN provider is required when the torrent downloader routes through the VPN" });

        string type = vpn.Type?.ToLowerInvariant() ?? "";
        if (type is not ("wireguard" or "openvpn"))
            errors.Add(new() { Path = "vpn.type", Message = "VPN type must be 'wireguard' or 'openvpn'" });
        else if (string.IsNullOrWhiteSpace(vpn.WireguardPrivateKey))
            errors.Add(new()
            {
                Path = "vpn.wireguardPrivateKey",
                Message = type == "wireguard"
                    ? "WireGuard private key is required when the torrent downloader routes through the VPN"
                    : "OpenVPN username is required when the torrent downloader routes through the VPN"
            });

        if (type == "wireguard" && string.IsNullOrWhiteSpace(vpn.WireguardAddresses))
            errors.Add(new() { Path = "vpn.wireguardAddresses", Message = "WireGuard addresses are required when the torrent downloader routes through the VPN" });
    }

    static bool TryGetBool(IDictionary dict, string key)
    {
        foreach (DictionaryEntry entry in dict)
        {
            if (entry.Key?.ToString()?.Equals(key, StringComparison.OrdinalIgnoreCase) != true)
                continue;
            return entry.Value switch
            {
                bool b => b,
                string s => bool.TryParse(s, out bool parsed) && parsed,
                _ => false
            };
        }
        return false;
    }

    void ValidateObject(
        Dictionary<string, object?> obj,
        JsonElement schemaProps,
        string path,
        List<ConfigValidationError> errors)
    {
        HashSet<string> allowedKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty prop in schemaProps.EnumerateObject())
            allowedKeys.Add(prop.Name);

        foreach (string key in obj.Keys)
        {
            if (!allowedKeys.Contains(key))
            {
                string fullPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";
                errors.Add(new() { Path = fullPath, Message = "unknown property (not in schema)" });
            }
        }

        foreach (JsonProperty prop in schemaProps.EnumerateObject())
        {
            string key = prop.Name;
            JsonElement propSchema = prop.Value;
            string fullPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";

            if (!obj.TryGetValue(key, out object? value) || value == null)
            {
                // Missing values fall back to the POCO/schema defaults, so an
                // omitted field is not an error. Present-but-empty fields are
                // still caught by the length/range/enum checks and by
                // FluentValidation on apply.
                continue;
            }

            ValidateValue(value, propSchema, fullPath, errors);
        }
    }

    void ValidateValue(object value, JsonElement schema, string path, List<ConfigValidationError> errors)
    {
        if (!schema.TryGetProperty("type", out JsonElement typeEl))
            return;

        string? typeName = typeEl.GetString();

        switch (typeName)
        {
            case "string" when value is string s:
                if (schema.TryGetProperty("minLength", out JsonElement minLen) && s.Length < minLen.GetInt32())
                    errors.Add(new() { Path = path, Message = $"must be at least {minLen.GetInt32()} characters" });
                if (schema.TryGetProperty("maxLength", out JsonElement maxLen) && s.Length > maxLen.GetInt32())
                    errors.Add(new() { Path = path, Message = $"must be at most {maxLen.GetInt32()} characters" });
                if (schema.TryGetProperty("enum", out JsonElement enumEl) && !MatchesEnum(s, enumEl))
                    errors.Add(new() { Path = path, Message = $"must be one of: {FormatEnum(enumEl)}" });
                break;

            case "integer":
                double numVal = Convert.ToDouble(value);
                ValidateNumber(numVal, schema, path, errors);
                break;

            case "number" when value is double or float or decimal or int or long:
                double doubleVal = Convert.ToDouble(value);
                ValidateNumber(doubleVal, schema, path, errors);
                break;

            case "array" when value is IList list:
                if (schema.TryGetProperty("items", out JsonElement itemSchema))
                {
                    int idx = 0;
                    foreach (object? item in list)
                    {
                        if (item != null)
                            ValidateValue(item, itemSchema, $"{path}[{idx}]", errors);
                        idx++;
                    }
                }
                break;

            case "object" when value is IDictionary dict:
                ValidateObjectValue(dict, schema, path, errors);
                break;

            case "object" when value is Dictionary<string, object?> typedDict:
                ValidateObjectValue(typedDict, schema, path, errors);
                break;
        }
    }

    static void ValidateNumber(double value, JsonElement schema, string path, List<ConfigValidationError> errors)
    {
        if (schema.TryGetProperty("minimum", out JsonElement min) && value < min.GetDouble())
            errors.Add(new() { Path = path, Message = $"must be >= {min.GetDouble()}" });
        if (schema.TryGetProperty("exclusiveMinimum", out JsonElement exMin) && value <= exMin.GetDouble())
            errors.Add(new() { Path = path, Message = $"must be > {exMin.GetDouble()}" });
        if (schema.TryGetProperty("maximum", out JsonElement max) && value > max.GetDouble())
            errors.Add(new() { Path = path, Message = $"must be <= {max.GetDouble()}" });
        if (schema.TryGetProperty("exclusiveMaximum", out JsonElement exMax) && value >= exMax.GetDouble())
            errors.Add(new() { Path = path, Message = $"must be < {exMax.GetDouble()}" });
    }

    static bool MatchesEnum(string value, JsonElement enumEl)
    {
        foreach (JsonElement option in enumEl.EnumerateArray())
        {
            if (option.ValueKind == JsonValueKind.String &&
                string.Equals(option.GetString(), value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static string FormatEnum(JsonElement enumEl) =>
        string.Join(", ", enumEl.EnumerateArray().Select(v => v.ToString()));

    void ValidateObjectValue(IDictionary dict, JsonElement schema, string path, List<ConfigValidationError> errors)
    {
        // A schema with explicit "properties" describes a strongly-typed object and must
        // reject unknown keys. A schema with an object-valued "additionalProperties" is a
        // free-form map keyed by the user, so any key is allowed.
        if (schema.TryGetProperty("properties", out JsonElement nestedProps))
        {
            Dictionary<string, object?> obj = new(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is string key)
                    obj[key] = entry.Value;
            }

            ValidateObject(obj, nestedProps, path, errors);
        }
        else if (schema.TryGetProperty("additionalProperties", out JsonElement additionalSchema) &&
                 additionalSchema.ValueKind == JsonValueKind.Object)
        {
            ValidateDictionary(dict, additionalSchema, path, errors);
        }
    }

    void ValidateDictionary(IDictionary dict, JsonElement additionalSchema, string path, List<ConfigValidationError> errors)
    {
        foreach (DictionaryEntry entry in dict)
        {
            string key = entry.Key.ToString()!;
            object? value = entry.Value;
            if (value != null)
                ValidateValue(value, additionalSchema, $"{path}.{key}", errors);
        }
    }
}

public class ConfigValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// Structured form of <see cref="Errors"/>. <see cref="ConfigValidationError.Path"/>
    /// is a camelCase dotted path (e.g. "services.tv.qualities.min") that the visual
    /// editor maps back to the offending field.
    /// </summary>
    public List<ConfigValidationError> Details { get; set; } = [];
}

public class ConfigValidationError
{
    public string Path { get; set; } = "";
    public string Message { get; set; } = "";

    public string Format() => string.IsNullOrEmpty(Path) ? Message : $"{Path}: {Message}";
}
