namespace Stackarr.Core.Configuration;

/// <summary>
/// The kind of control the visual editor should render for a node.
/// Derived purely from the JSON schema, with no knowledge of any module.
/// </summary>
public enum ConfigFormFieldType
{
    String,
    Boolean,
    Integer,
    Number,
    Enum,
    Object,
    Array,
    Dictionary
}

/// <summary>
/// Base node in the schema-driven config form tree. Nodes own their current
/// value directly so the tree can be bound and mutated by the UI, then
/// serialized straight back into a YAML document.
/// </summary>
public abstract class ConfigFormNode
{
    /// <summary>camelCase YAML key for this field.</summary>
    public string Key { get; set; } = "";

    /// <summary>Dotted camelCase path, e.g. "services.tv.qualities.min".</summary>
    public string Path { get; set; } = "";

    /// <summary>Human readable label.</summary>
    public string Title { get; set; } = "";

    /// <summary>Help text shown under the control.</summary>
    public string Description { get; set; } = "";

    public ConfigFormFieldType FieldType { get; set; }

    /// <summary>Raw JSON schema type: string | boolean | integer | number | object | array.</summary>
    public string Type { get; set; } = "string";

    /// <summary>JSON schema format (e.g. "password").</summary>
    public string? Format { get; set; }

    public bool Required { get; set; }

    public bool IsSecret => string.Equals(Format, "password", StringComparison.OrdinalIgnoreCase);

    public object? DefaultValue { get; set; }

    public List<string> EnumValues { get; set; } = [];

    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public double? ExclusiveMinimum { get; set; }
    public double? ExclusiveMaximum { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }

    public abstract ConfigFormNode Clone();

    protected void CopyBaseTo(ConfigFormNode target)
    {
        target.Key = Key;
        target.Path = Path;
        target.Title = Title;
        target.Description = Description;
        target.FieldType = FieldType;
        target.Type = Type;
        target.Format = Format;
        target.Required = Required;
        target.DefaultValue = DefaultValue;
        target.EnumValues = [.. EnumValues];
        target.Minimum = Minimum;
        target.Maximum = Maximum;
        target.ExclusiveMinimum = ExclusiveMinimum;
        target.ExclusiveMaximum = ExclusiveMaximum;
        target.MinLength = MinLength;
        target.MaxLength = MaxLength;
    }
}

/// <summary>A single string/boolean/integer/number/enum value.</summary>
public sealed class ConfigScalarNode : ConfigFormNode
{
    public object? Value { get; set; }

    public override ConfigFormNode Clone()
    {
        ConfigScalarNode clone = new() { Value = Value };
        CopyBaseTo(clone);
        return clone;
    }
}

/// <summary>A nested object section with typed children.</summary>
public class ConfigObjectNode : ConfigFormNode
{
    public List<ConfigFormNode> Children { get; set; } = [];

    public override ConfigFormNode Clone()
    {
        ConfigObjectNode clone = new() { Children = Children.Select(c => c.Clone()).ToList() };
        CopyBaseTo(clone);
        return clone;
    }
}

/// <summary>A list field. Items are scalar or object nodes; <see cref="ItemPrototype"/>
/// is a template used when adding new entries.</summary>
public sealed class ConfigArrayNode : ConfigFormNode
{
    public List<ConfigFormNode> Items { get; set; } = [];
    public ConfigFormNode? ItemPrototype { get; set; }

    public override ConfigFormNode Clone()
    {
        ConfigArrayNode clone = new()
        {
            Items = Items.Select(i => i.Clone()).ToList(),
            ItemPrototype = ItemPrototype?.Clone()
        };
        CopyBaseTo(clone);
        return clone;
    }
}

/// <summary>A free-form map field (e.g. routes.customRoutes, codec scores).</summary>
public sealed class ConfigDictionaryNode : ConfigFormNode
{
    public List<ConfigDictionaryEntry> Entries { get; set; } = [];
    public ConfigFormNode? ValuePrototype { get; set; }

    public override ConfigFormNode Clone()
    {
        ConfigDictionaryNode clone = new()
        {
            Entries = Entries.Select(e => new ConfigDictionaryEntry { Key = e.Key, Value = e.Value.Clone() }).ToList(),
            ValuePrototype = ValuePrototype?.Clone()
        };
        CopyBaseTo(clone);
        return clone;
    }
}

public sealed class ConfigDictionaryEntry
{
    public string Key { get; set; } = "";
    public ConfigFormNode Value { get; set; } = new ConfigScalarNode();
}

/// <summary>A free-form value (e.g. implementation-specific indexer settings)
/// that cannot be represented as a structured control. The value is preserved
/// verbatim and surfaced read-only with a pointer to the YAML editor.</summary>
public sealed class ConfigRawNode : ConfigFormNode
{
    public object? Value { get; set; }

    public override ConfigFormNode Clone()
    {
        ConfigRawNode clone = new() { Value = ConfigFormBuilder.DeepCloneValue(Value) };
        CopyBaseTo(clone);
        return clone;
    }
}

/// <summary>A top-level config section (e.g. Storage, Routes, TV).</summary>
public sealed class ConfigFormSection : ConfigObjectNode
{
    /// <summary>Registry section path, e.g. "services:tv". Empty for the general root fields.</summary>
    public string SectionPath { get; set; } = "";

    /// <summary>Whether this is a service section (structural "services:" path).</summary>
    public bool IsService { get; set; }

    /// <summary>Icon name supplied by the section owner, resolved generically by the UI.</summary>
    public string Icon { get; set; } = "";

    /// <summary>UI group supplied by the section owner (e.g. "Infrastructure", "Services").</summary>
    public string Group { get; set; } = "General";

    /// <summary>Ordering hint for the group (lower first).</summary>
    public int GroupOrder { get; set; }

    /// <summary>Ordering hint for this section within its group (lower first).</summary>
    public int Order { get; set; }
}

/// <summary>A visual grouping of related sections.</summary>
public sealed class ConfigFormGroup
{
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<ConfigFormSection> Sections { get; set; } = [];
}

/// <summary>The complete editable form tree.</summary>
public sealed class ConfigFormModel
{
    public List<ConfigFormGroup> Groups { get; set; } = [];

    public IEnumerable<ConfigFormSection> AllSections => Groups.SelectMany(g => g.Sections);

    /// <summary>
    /// Sections that should appear in the visual editor navigation. A section whose path is a
    /// child of another registered section is represented inside the parent's nested object, so
    /// showing it separately would duplicate controls and let stale child values overwrite edits.
    /// </summary>
    public IEnumerable<ConfigFormSection> TopLevelSections => AllSections.Where(section => !IsSubsection(section));

    public bool IsSubsection(ConfigFormSection section) =>
        !string.IsNullOrEmpty(section.SectionPath) &&
        AllSections.Any(parent =>
            !ReferenceEquals(parent, section) &&
            !string.IsNullOrEmpty(parent.SectionPath) &&
            section.SectionPath.StartsWith(parent.SectionPath + ":", StringComparison.OrdinalIgnoreCase));

    public ConfigFormSection? FindSection(string sectionPath) =>
        AllSections.FirstOrDefault(s => s.SectionPath.Equals(sectionPath, StringComparison.OrdinalIgnoreCase));
}
