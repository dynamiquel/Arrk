using Microsoft.Extensions.Configuration;
using Stackarr.Core.Configuration;
using Stackarr.Core.Services;
using System.Text.Json.Nodes;

namespace Stackarr.Ui;

/// <summary>The per-account dashboard document. YAML is the source of truth; the Tile Editor is a
/// structured view over this model.</summary>
public sealed class DashboardLayout
{
    public int Version { get; set; } = 1;

    public List<TileSection> Sections { get; set; } = [];
}

/// <summary>A named group of tiles on the dashboard.</summary>
public sealed class TileSection
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public List<DashboardTile> Tiles { get; set; } = [];
}

/// <summary>One tile reference inside a section, with an optional per-placement size override.</summary>
public sealed class DashboardTile
{
    public string Id { get; set; } = "";

    public TileSize? Size { get; set; }
}

/// <summary>A user-facing problem with a dashboard layout, shown by the Tile Editor.</summary>
public sealed record DashboardLayoutError(string Message);

/// <summary>
/// Loads, validates and saves dashboard layouts. A layout is stored per account as YAML in
/// <see cref="UserUiPreference"/>; a new account starts from the curated default layout.
/// </summary>
public sealed class DashboardLayoutService(
    UserUiPreferenceService preferences,
    TileRegistry registry,
    IConfiguration configuration)
{
    public const int MaxSections = 20;
    public const int MaxTilesPerSection = 40;
    public const int MaxTiles = 200;
    public const int MaxSectionTitleLength = 80;
    public const int MaxYamlLength = 65_536;

    /// <summary>The account's layout, or the curated default when nothing has been saved.</summary>
    public async Task<DashboardLayout> GetAsync(int userId, bool isAdmin, CancellationToken ct = default)
    {
        UserUiPreference? preference = await preferences.GetAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(preference?.DashboardYaml))
            return CreateDefault(isAdmin);

        try
        {
            DashboardLayout? layout = Parse(preference.DashboardYaml);
            return layout ?? CreateDefault(isAdmin);
        }
        catch (Exception)
        {
            // A hand-edited document that no longer parses must not blank the dashboard. The editor
            // still shows the saved YAML so the user can fix it.
            return CreateDefault(isAdmin);
        }
    }

    /// <summary>The raw saved YAML, or the serialized default when nothing has been saved.</summary>
    public async Task<string> GetYamlAsync(int userId, bool isAdmin, CancellationToken ct = default)
    {
        UserUiPreference? preference = await preferences.GetAsync(userId, ct);
        if (!string.IsNullOrWhiteSpace(preference?.DashboardYaml))
            return preference.DashboardYaml;

        return Serialize(CreateDefault(isAdmin));
    }

    /// <summary>Parses YAML and validates it against the current tile collection.</summary>
    public DashboardLayout ParseAndValidate(string yaml, bool isAdmin)
    {
        DashboardLayout layout = Parse(yaml) ?? new();
        IReadOnlyList<DashboardLayoutError> errors = Validate(layout, isAdmin);
        if (errors.Count > 0)
            throw new DashboardLayoutException(errors);

        return layout;
    }

    public DashboardLayout? Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return null;

        DashboardLayout? layout = YamlConfigSerializer.Deserialize<DashboardLayout>(yaml);
        if (layout is null)
            return null;

        layout.Sections ??= [];
        foreach (TileSection section in layout.Sections)
            section.Tiles ??= [];

        return layout;
    }

    public string Serialize(DashboardLayout layout) => YamlConfigSerializer.Serialize(layout);

    /// <summary>Validates and saves a layout. Throws <see cref="DashboardLayoutException"/> on error.</summary>
    public async Task SaveAsync(int userId, DashboardLayout layout, bool isAdmin, CancellationToken ct = default)
    {
        IReadOnlyList<DashboardLayoutError> errors = Validate(layout, isAdmin);
        if (errors.Count > 0)
            throw new DashboardLayoutException(errors);

        string yaml = Serialize(Normalize(layout));
        if (yaml.Length > MaxYamlLength)
            throw new DashboardLayoutException([new($"The layout is too large (maximum {MaxYamlLength} characters).")]);

        await preferences.SaveDashboardAsync(userId, yaml, ct);
    }

    /// <summary>Saves raw YAML after validating it, so the stored document is always canonical.</summary>
    public async Task SaveYamlAsync(int userId, string yaml, bool isAdmin, CancellationToken ct = default)
    {
        DashboardLayout layout = ParseAndValidate(yaml, isAdmin);
        await SaveAsync(userId, layout, isAdmin, ct);
    }

    public Task ResetAsync(int userId, CancellationToken ct = default) =>
        preferences.ClearDashboardAsync(userId, ct);

    /// <summary>
    /// The cached catalogue, available in the Tile Editor. Keeping this here means pages never
    /// reach into <see cref="TileRegistry"/> directly for labels.
    /// </summary>
    public IReadOnlyList<TileDefinition> AvailableTiles(bool isAdmin) => registry.Available(isAdmin);

    public TileDefinition? FindTile(string id) => registry.Find(id);

    /// <summary>
    /// JSON schema for the Tile Editor's YAML view. It teaches the shared Monaco editor the document
    /// shape, the enum of available tile ids, their descriptions and the standard sizes, so the
    /// editor completes and validates against the live tile collection.
    /// </summary>
    public string BuildSchema(bool isAdmin)
    {
        List<TileDefinition> tiles = registry.Available(isAdmin).ToList();
        JsonArray tileIds = [];
        JsonArray tileDescriptions = [];
        foreach (TileDefinition tile in tiles)
        {
            tileIds.Add(tile.Id);
            tileDescriptions.Add($"{tile.Category} — {tile.Title}: {tile.Description ?? "Tile"}");
        }

        JsonObject tileSchema = new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("id"),
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Stable tile id from the shared collection.",
                    ["enum"] = tileIds,
                    ["enumDescriptions"] = tileDescriptions
                },
                ["size"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Standard tile size. The section grid decides how many fit per row.",
                    ["enum"] = new JsonArray("Small", "Medium", "Large", "VeryLarge"),
                    ["enumDescriptions"] = new JsonArray(
                        "Compact card (about four per row).",
                        "Wide card (about two per row).",
                        "Double the Medium width (about half the row).",
                        "Full-width card (one per row)."),
                    ["default"] = "Small"
                }
            }
        };

        JsonObject sectionSchema = new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("id", "title", "tiles"),
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Stable section id (letters, digits and dashes).",
                    ["pattern"] = "^[a-z0-9]+(?:-[a-z0-9]+)*$"
                },
                ["title"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Heading shown above the section.",
                    ["minLength"] = 1,
                    ["maxLength"] = MaxSectionTitleLength
                },
                ["tiles"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Tiles in this section, in display order.",
                    ["maxItems"] = MaxTilesPerSection,
                    ["items"] = tileSchema
                }
            }
        };

        JsonObject root = new()
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "Stackarr dashboard layout",
            ["description"] = "Per-account dashboard Tile Sections. The Tile Editor writes this document.",
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("version", "sections"),
            ["properties"] = new JsonObject
            {
                ["version"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Dashboard layout version.",
                    ["enum"] = new JsonArray(1),
                    ["default"] = 1
                },
                ["sections"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Ordered Tile Sections.",
                    ["maxItems"] = MaxSections,
                    ["items"] = sectionSchema
                }
            }
        };

        return root.ToJsonString();
    }

    /// <summary>
    /// The layout a new account starts with. It comes from <c>ui.defaultDashboardSections</c> when
    /// an administrator has set one, otherwise from the built-in default. Unknown, disabled and
    /// admin-only tiles are dropped so the same config works for every account.
    /// </summary>
    public DashboardLayout CreateDefault(bool isAdmin)
    {
        List<DashboardSectionConfig> configured = configuration
            .GetSection(UiConfig.SectionName)
            .GetSection("defaultDashboardSections")
            .Get<List<DashboardSectionConfig>>() ?? UiDefaults.CreateDefaultSections();
        DashboardLayout source = new() { Version = 1 };

        foreach (DashboardSectionConfig section in configured)
        {
            source.Sections.Add(new TileSection
            {
                Title = section.Title,
                Tiles =
                [
                    .. section.Tiles.Select(tile => new DashboardTile
                    {
                        Id = tile.Id,
                        Size = ToTileSize(tile.Size)
                    })
                ]
            });
        }

        return FilterDefault(source, isAdmin);
    }

    static TileSize ToTileSize(DashboardTileSize size) => size switch
    {
        DashboardTileSize.Small => TileSize.Small,
        DashboardTileSize.Large => TileSize.Large,
        DashboardTileSize.VeryLarge => TileSize.VeryLarge,
        _ => TileSize.Medium
    };

    DashboardLayout FilterDefault(DashboardLayout source, bool isAdmin)
    {
        DashboardLayout normalized = Normalize(source);
        DashboardLayout filtered = new() { Version = 1 };

        foreach (TileSection section in normalized.Sections)
        {
            List<DashboardTile> tiles = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (DashboardTile tile in section.Tiles)
            {
                TileDefinition? definition = registry.Find(tile.Id);
                if (definition is null
                    || !registry.IsAvailable(definition.EnabledConfigPath)
                    || (!isAdmin && definition.AdminOnly)
                    || !seen.Add(definition.Id))
                {
                    continue;
                }

                tiles.Add(new DashboardTile
                {
                    Id = definition.Id,
                    Size = tile.Size ?? definition.DefaultSize
                });
            }

            if (tiles.Count == 0)
                continue;

            filtered.Sections.Add(new TileSection
            {
                Id = section.Id,
                Title = section.Title,
                Tiles = tiles
            });
        }

        return filtered;
    }

    public IReadOnlyList<DashboardLayoutError> Validate(DashboardLayout layout, bool isAdmin)
    {
        List<DashboardLayoutError> errors = [];
        if (layout.Version != 1)
            errors.Add(new("Unsupported layout version. Expected version 1."));

        if (layout.Sections.Count > MaxSections)
            errors.Add(new($"A dashboard may contain at most {MaxSections} tile sections."));

        HashSet<string> sectionIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> tileIds = new(StringComparer.OrdinalIgnoreCase);
        int totalTiles = 0;

        foreach (TileSection section in layout.Sections)
        {
            string title = section.Title?.Trim() ?? "";
            if (title.Length == 0)
                errors.Add(new("Every tile section needs a title."));
            else if (title.Length > MaxSectionTitleLength)
                errors.Add(new($"Tile section '{title}' is too long (maximum {MaxSectionTitleLength} characters)."));

            if (string.IsNullOrWhiteSpace(section.Id))
                errors.Add(new($"Tile section '{title}' needs an id."));
            else if (!sectionIds.Add(section.Id.Trim()))
                errors.Add(new($"Duplicate tile section id '{section.Id}'."));

            if (section.Tiles.Count > MaxTilesPerSection)
                errors.Add(new($"Tile section '{title}' has too many tiles (maximum {MaxTilesPerSection})."));

            foreach (DashboardTile tile in section.Tiles)
            {
                string id = tile.Id?.Trim() ?? "";
                if (id.Length == 0)
                {
                    errors.Add(new($"Tile section '{title}' contains a tile with no id."));
                    continue;
                }

                if (!tileIds.Add(id))
                    errors.Add(new($"Tile '{id}' appears more than once."));

                TileDefinition? definition = registry.Find(id);
                if (definition is null)
                    errors.Add(new($"Unknown tile '{id}'."));
                else if (!registry.IsAvailable(definition.EnabledConfigPath))
                    errors.Add(new($"Tile '{id}' is not available because its service is disabled."));
                else if (!isAdmin && definition.AdminOnly)
                    errors.Add(new($"Tile '{id}' is only available to administrators."));
            }

            totalTiles += section.Tiles.Count;
        }

        if (totalTiles > MaxTiles)
            errors.Add(new($"A dashboard may contain at most {MaxTiles} tiles."));

        return errors;
    }

    /// <summary>Trims user-entered values and fills stable section ids before saving.</summary>
    public static DashboardLayout Normalize(DashboardLayout layout)
    {
        DashboardLayout normalized = new() { Version = 1 };
        HashSet<string> usedIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (TileSection section in layout.Sections)
        {
            string title = section.Title?.Trim() ?? "";
            string id = string.IsNullOrWhiteSpace(section.Id) ? Slug(title) : Slug(section.Id.Trim());
            if (id.Length == 0 || !usedIds.Add(id))
            {
                string baseId = id.Length == 0 ? "section" : id;
                int suffix = 2;
                while (!usedIds.Add($"{baseId}-{suffix}"))
                    suffix++;
                id = $"{baseId}-{suffix}";
            }

            normalized.Sections.Add(new()
            {
                Id = id,
                Title = title,
                Tiles = [.. section.Tiles
                    .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                    .Select(t => new DashboardTile { Id = t.Id.Trim(), Size = t.Size })]
            });
        }

        return normalized;
    }

    static string Slug(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        bool lastDash = false;
        foreach (char c in value.Trim())
        {
            char next = char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-';
            if (next == '-' && (length == 0 || lastDash))
                continue;

            buffer[length++] = next;
            lastDash = next == '-';
        }

        return new string(buffer[..length]).Trim('-');
    }
}

/// <summary>Thrown when a dashboard layout cannot be parsed or validated.</summary>
public sealed class DashboardLayoutException(IReadOnlyList<DashboardLayoutError> errors)
    : Exception(string.Join(" ", errors.Select(e => e.Message)))
{
    public IReadOnlyList<DashboardLayoutError> Errors { get; } = errors;
}
