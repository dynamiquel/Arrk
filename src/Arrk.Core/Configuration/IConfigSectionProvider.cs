namespace Arrk.Core.Configuration;

/// <summary>
/// Describes a configuration section that the app knows about at runtime.
/// Each module registers one of these for its config POCO and supplies its own
/// UI metadata (icon, group, ordering), keeping the UI module-agnostic: the UI
/// renders whatever the registry describes without knowing which module it is.
/// Core registers its own for Storage, Vpn, Routes, etc.
/// </summary>
public interface IConfigSectionProvider
{
    /// <summary>
    /// Full configuration path, e.g. "storage", "vpn", "services:tv".
    /// </summary>
    string SectionPath { get; }

    /// <summary>
    /// Human-readable name for UI display, e.g. "Storage", "VPN", "TV".
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// The POCO type for this config section (e.g. typeof(StorageConfig)).
    /// Used for DI binding, validation, schema generation, and default instantiation.
    /// </summary>
    Type ConfigType { get; }

    /// <summary>
    /// Whether this section should appear in the config editor.
    /// App-internal sections (e.g. internal monitoring) return false.
    /// </summary>
    bool IsUserEditable { get; }

    /// <summary>
    /// Icon name the section owner wants shown for this section, resolved by the
    /// UI from its icon set (e.g. "Storage", "Tv", "Download"). Modules pick this
    /// themselves so the UI never needs module-specific knowledge. Unknown names
    /// fall back to a generic icon.
    /// </summary>
    string Icon { get; }

    /// <summary>
    /// UI group this section is shown under, e.g. "Infrastructure" or "Services".
    /// Sections sharing a group are rendered together.
    /// </summary>
    string Group { get; }

    /// <summary>Ordering hint for the section within its group (lower first).</summary>
    int Order { get; }

    /// <summary>Ordering hint for the group in the UI (lower first).</summary>
    int GroupOrder { get; }
}
