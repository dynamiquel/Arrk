using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;

namespace Stackarr.Core.Configuration;

/// <summary>
/// Generates a JSON Schema from registered config POCOs, enriched with
/// constraints from FluentValidation rules, DataAnnotation attributes, defaults
/// and allowed values. Used for live editor validation, autocomplete suggestions
/// and the schema-driven visual (form) config editor.
/// </summary>
/// <remarks>
/// The schema mirrors the real YAML document shape: sections registered as
/// "services:tv" are emitted under a nested "services" object. This keeps the
/// visual editor and the YAML editor reading the exact same structure.
/// </remarks>
public class ConfigSchemaGenerator(
    ConfigSectionRegistry registry,
    IServiceProvider serviceProvider)
{
    /// <summary>
    /// Generate the full JSON Schema for all registered config sections.
    /// </summary>
    public JsonElement GenerateSchema()
    {
        Dictionary<string, object?> root = new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false
        };

        Dictionary<string, object?> properties = new();

        foreach (IConfigSectionProvider provider in registry.GetAll()
                     .OrderBy(p => p.SectionPath, StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<string, object?> sectionSchema = BuildTypeSchema(provider.ConfigType);
            sectionSchema["title"] = provider.DisplayName;
            InsertSection(properties, provider.SectionPath, sectionSchema);
        }

        AddRootPropertySchemas(properties);

        root["properties"] = properties;
        return Serialize(root);
    }

    /// <summary>
    /// Build JSON Schema for a single POCO type (object with typed properties).
    /// </summary>
    public Dictionary<string, object?> BuildTypeSchema(Type type)
    {
        Dictionary<string, object?> schema = new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false
        };
        Dictionary<string, object?> properties = new();

        object? instance = TryCreateDefaultInstance(type);

        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            string yamlKey = YamlConfigSerializer.ToCamelCase(prop.Name);
            object? value = SafeGetValue(prop, instance);
            properties[yamlKey] = BuildPropertySchema(prop, value);
        }

        schema["properties"] = properties;
        return schema;
    }

    /// <summary>
    /// Add the top-level <see cref="StackarrConfig"/> scalar properties (e.g. tmdbApiKey)
    /// that are not covered by a registered section provider so the schema describes the
    /// complete root document.
    /// </summary>
    void AddRootPropertySchemas(Dictionary<string, object?> properties)
    {
        object? defaults = TryCreateDefaultInstance(typeof(StackarrConfig));
        if (defaults == null)
            return;

        foreach (PropertyInfo prop in typeof(StackarrConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            string key = YamlConfigSerializer.ToCamelCase(prop.Name);
            if (properties.ContainsKey(key))
                continue;

            properties[key] = BuildPropertySchema(prop, SafeGetValue(prop, defaults));
        }
    }

    static void InsertSection(
        Dictionary<string, object?> rootProperties,
        string sectionPath,
        Dictionary<string, object?> sectionSchema)
    {
        string[] segments = sectionPath
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(YamlConfigSerializer.ToCamelCase)
            .ToArray();

        if (segments.Length == 0)
            return;

        Dictionary<string, object?> current = rootProperties;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            string key = segments[i];

            if (current.TryGetValue(key, out object? existing) &&
                existing is Dictionary<string, object?> existingSchema &&
                existingSchema.TryGetValue("properties", out object? existingProps) &&
                existingProps is Dictionary<string, object?> existingProperties)
            {
                current = existingProperties;
                continue;
            }

            Dictionary<string, object?> nestedProperties = new();
            Dictionary<string, object?> nested = new()
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["title"] = Humanize(key),
                ["properties"] = nestedProperties
            };
            current[key] = nested;
            current = nestedProperties;
        }

        current[segments[^1]] = sectionSchema;
    }

    Dictionary<string, object?> BuildPropertySchema(PropertyInfo prop, object? value)
    {
        Dictionary<string, object?> schema = BuildSchemaForType(prop.PropertyType, value);

        DisplayAttribute? display = prop.GetCustomAttribute<DisplayAttribute>();
        schema["title"] = !string.IsNullOrEmpty(display?.Name)
            ? display!.Name!
            : Humanize(prop.Name);

        DescriptionAttribute? desc = prop.GetCustomAttribute<DescriptionAttribute>();
        string? description = desc?.Description;
        if (string.IsNullOrEmpty(description) && !string.IsNullOrEmpty(display?.Description))
            description = display!.Description;
        if (!string.IsNullOrEmpty(description))
            schema["description"] = description;

        object? defaultValue = ToSchemaValue(value, prop.PropertyType);
        if (defaultValue != null)
            schema["default"] = defaultValue;

        ApplyDataAnnotationConstraints(schema, prop);

        List<ValidationRuleInfo> rules = GetFluentValidationRules(prop);
        ApplyFluentValidationRules(schema, rules, prop.PropertyType);

        return schema;
    }

    /// <summary>
    /// Build the schema node for a CLR type: scalars, enums, arrays, dictionaries
    /// and nested objects.
    /// </summary>
    Dictionary<string, object?> BuildSchemaForType(Type type, object? value)
    {
        if (type == typeof(object))
            return new Dictionary<string, object?>();

        if (type == typeof(string))
            return new() { ["type"] = "string" };

        if (type == typeof(bool))
            return new() { ["type"] = "boolean" };

        if (type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong))
            return new() { ["type"] = "integer" };

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new() { ["type"] = "number" };

        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null && underlying.IsEnum)
            return BuildEnumSchema(underlying);

        if (type.IsEnum)
            return BuildEnumSchema(type);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            Type valueType = type.GetGenericArguments()[1];
            return new()
            {
                ["type"] = "object",
                ["additionalProperties"] = BuildSchemaForType(valueType, null)
            };
        }

        if (type.IsArray)
        {
            Type elementType = type.GetElementType() ?? typeof(object);
            return new()
            {
                ["type"] = "array",
                ["items"] = BuildSchemaForType(elementType, null)
            };
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            Type itemType = type.GetGenericArguments()[0];
            return new()
            {
                ["type"] = "array",
                ["items"] = BuildSchemaForType(itemType, null)
            };
        }

        if (type.IsClass)
            return BuildTypeSchema(type);

        return new() { ["type"] = "string" };
    }

    static Dictionary<string, object?> BuildEnumSchema(Type enumType)
    {
        List<string> names = Enum.GetNames(enumType)
            .Select(YamlConfigSerializer.ToCamelCase)
            .ToList();

        return new()
        {
            ["type"] = "string",
            ["enum"] = names
        };
    }

    static void ApplyDataAnnotationConstraints(Dictionary<string, object?> schema, PropertyInfo prop)
    {
        RequiredAttribute? required = prop.GetCustomAttribute<RequiredAttribute>();
        if (required != null && schema.TryGetValue("type", out object? type) && (string?)type == "string")
            schema["minLength"] = 1;

        StringLengthAttribute? length = prop.GetCustomAttribute<StringLengthAttribute>();
        if (length != null)
        {
            if (length.MinimumLength > 0)
                schema["minLength"] = length.MinimumLength;
            if (length.MaximumLength > 0 && length.MaximumLength < int.MaxValue)
                schema["maxLength"] = length.MaximumLength;
        }

        MinLengthAttribute? minLength = prop.GetCustomAttribute<MinLengthAttribute>();
        if (minLength != null && minLength.Length > 0)
            schema["minLength"] = minLength.Length;

        MaxLengthAttribute? maxLength = prop.GetCustomAttribute<MaxLengthAttribute>();
        if (maxLength != null && maxLength.Length > 0)
            schema["maxLength"] = maxLength.Length;

        RangeAttribute? range = prop.GetCustomAttribute<RangeAttribute>();
        if (range != null)
        {
            if (range.Minimum is int min)
                schema["minimum"] = min;
            if (range.Maximum is int max)
                schema["maximum"] = max;
        }

        AllowedValuesAttribute? allowed = prop.GetCustomAttribute<AllowedValuesAttribute>();
        if (allowed?.Values != null)
        {
            schema["enum"] = allowed.Values
                .Select(v => Convert.ToString(v) ?? "")
                .Where(v => v.Length > 0)
                .ToList();
        }

        DataTypeAttribute? dataType = prop.GetCustomAttribute<DataTypeAttribute>();
        if (dataType != null && dataType.DataType == DataType.Password)
            schema["format"] = "password";
    }

    List<ValidationRuleInfo> GetFluentValidationRules(PropertyInfo prop)
    {
        List<ValidationRuleInfo> rules = new();

        Type? declaringType = prop.DeclaringType;
        if (declaringType == null)
            return rules;

        Type validatorType = typeof(IValidator<>).MakeGenericType(declaringType);
        if (serviceProvider.GetService(validatorType) is not IValidator validator)
            return rules;

        IValidatorDescriptor descriptor = validator.CreateDescriptor();
        string propName = prop.Name;

        foreach (IValidationRule rule in descriptor.GetRulesForMember(propName))
        {
            // Skip rules guarded by a condition (e.g. When(x => x.Vpn.Enabled)); the schema
            // cannot express the condition, so surfacing them would mark fields as required
            // in cases where they are not.
            if (rule.HasCondition || rule.HasAsyncCondition)
                continue;

            foreach (IRuleComponent component in rule.Components)
            {
                ValidationRuleInfo? info = ExtractRuleInfo(component.Validator);
                if (info != null)
                    rules.Add(info);
            }
        }

        return rules;
    }

    static ValidationRuleInfo? ExtractRuleInfo(IPropertyValidator validator)
    {
        string typeName = validator.GetType().Name.Split('`')[0];

        if (typeName == "NotEmptyValidator")
            return new() { Kind = RuleKind.NotEmpty };

        PropertyInfo? valueToCompare = validator.GetType().GetProperty(
            "ValueToCompare", BindingFlags.Public | BindingFlags.Instance);

        if (valueToCompare != null)
        {
            object? val = valueToCompare.GetValue(validator);
            if (val == null)
                return null;

            string? comparison = validator.GetType().GetProperty(
                "Comparison", BindingFlags.Public | BindingFlags.Instance)?.GetValue(validator)?.ToString();

            return comparison switch
            {
                "GreaterThan" => new() { Kind = RuleKind.GreaterThan, Value = Convert.ToDouble(val) },
                "GreaterThanOrEqual" => new() { Kind = RuleKind.GreaterThanOrEqualTo, Value = Convert.ToDouble(val) },
                "LessThan" => new() { Kind = RuleKind.LessThan, Value = Convert.ToDouble(val) },
                "LessThanOrEqual" => new() { Kind = RuleKind.LessThanOrEqualTo, Value = Convert.ToDouble(val) },
                _ => null
            };
        }

        PropertyInfo? fromProp = validator.GetType().GetProperty("From", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo? toProp = validator.GetType().GetProperty("To", BindingFlags.Public | BindingFlags.Instance);
        if (fromProp != null && toProp != null)
        {
            object? from = fromProp.GetValue(validator);
            object? to = toProp.GetValue(validator);
            if (from != null && to != null)
            {
                bool isExclusive = typeName.Contains("Exclusive");
                return new()
                {
                    Kind = isExclusive ? RuleKind.ExclusiveBetween : RuleKind.InclusiveBetween,
                    Value = Convert.ToDouble(from),
                    Value2 = Convert.ToDouble(to)
                };
            }
        }

        return null;
    }

    static void ApplyFluentValidationRules(Dictionary<string, object?> schema, List<ValidationRuleInfo> rules, Type propType)
    {
        foreach (ValidationRuleInfo rule in rules)
        {
            switch (rule.Kind)
            {
                case RuleKind.NotEmpty when propType == typeof(string):
                    schema["minLength"] = 1;
                    break;
                case RuleKind.GreaterThan:
                    schema["exclusiveMinimum"] = rule.Value;
                    break;
                case RuleKind.GreaterThanOrEqualTo:
                    schema["minimum"] = rule.Value;
                    break;
                case RuleKind.LessThan:
                    schema["exclusiveMaximum"] = rule.Value;
                    break;
                case RuleKind.LessThanOrEqualTo:
                    schema["maximum"] = rule.Value;
                    break;
                case RuleKind.InclusiveBetween:
                    schema["minimum"] = rule.Value;
                    schema["maximum"] = rule.Value2;
                    break;
                case RuleKind.ExclusiveBetween:
                    schema["exclusiveMinimum"] = rule.Value;
                    schema["exclusiveMaximum"] = rule.Value2;
                    break;
            }
        }
    }

    /// <summary>
    /// Convert a default value into a JSON-schema friendly value. Complex objects
    /// (and collections containing them) return null because their structure is
    /// already described by the nested schema.
    /// </summary>
    static object? ToSchemaValue(object? value, Type type)
    {
        if (value == null)
            return null;

        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsEnum)
        {
            string? name = Enum.GetName(underlying, value);
            return name == null ? null : YamlConfigSerializer.ToCamelCase(name);
        }

        switch (value)
        {
            case string s:
                return s;
            case bool b:
                return b;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return Convert.ToInt64(value);
            case float or double or decimal:
                return Convert.ToDouble(value);
        }

        if (value is IDictionary dict)
        {
            Dictionary<string, object?> result = new();
            foreach (DictionaryEntry entry in dict)
            {
                string key = entry.Key?.ToString() ?? "";
                object? child = ToSchemaValue(entry.Value, entry.Value?.GetType() ?? typeof(object));
                if (child == null && entry.Value != null)
                    return null;
                result[key] = child;
            }
            return result;
        }

        if (value is IList list)
        {
            List<object?> result = new();
            foreach (object? item in list)
            {
                object? child = ToSchemaValue(item, item?.GetType() ?? typeof(object));
                if (child == null && item != null)
                    return null;
                result.Add(child);
            }
            return result;
        }

        return null;
    }

    static object? SafeGetValue(PropertyInfo prop, object? instance)
    {
        if (instance == null)
            return null;

        try
        {
            return prop.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    static object? TryCreateDefaultInstance(Type type)
    {
        if (type.IsAbstract || type.IsInterface)
            return null;

        try
        {
            return Activator.CreateInstance(type);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Turn a property/section name into a human readable label
    /// (e.g. "wireguardPrivateKey" -> "Wireguard Private Key").
    /// </summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        StringBuilder sb = new();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool newWord = char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1]);
            if (newWord && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }

        string result = sb.ToString().Replace('_', ' ');
        return result.Trim();
    }

    static JsonElement Serialize(object obj)
    {
        return JsonSerializer.SerializeToElement(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }
}

internal class ValidationRuleInfo
{
    public RuleKind Kind { get; set; }
    public double Value { get; set; }
    public double Value2 { get; set; }
}

internal enum RuleKind
{
    NotEmpty,
    GreaterThan,
    GreaterThanOrEqualTo,
    LessThan,
    LessThanOrEqualTo,
    InclusiveBetween,
    ExclusiveBetween
}
