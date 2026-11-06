using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Stackarr.Core.Configuration;

namespace Stackarr.Ui;

/// <summary>
/// The composed tile collection: every <see cref="ITileProvider"/> contribution, filtered live by
/// its config section. Duplicate ids fail fast, because a dashboard layout stores ids and could
/// otherwise resolve to the wrong widget. Role filtering is applied at render time so a layout can
/// keep an admin tile in place for an admin while a non-admin simply does not see it.
/// </summary>
public sealed class TileRegistry : IDisposable
{
    readonly IReadOnlyList<TileDefinition> _tiles;
    readonly IConfiguration _configuration;
    readonly IDisposable _reloadSubscription;

    public TileRegistry(IEnumerable<ITileProvider> providers, IConfiguration configuration)
    {
        _configuration = configuration;
        // A provider's list order is the catalogue order: there is no separate Order property to
        // keep in sync. Category groups the editor; within a category the provider and list
        // position decide.
        _tiles = providers
            .SelectMany(p => p.Tiles)
            .Select((tile, index) => (Tile: tile, Index: index))
            .OrderBy(x => x.Tile.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Index)
            .Select(x => x.Tile)
            .ToList();

        string[] duplicates = _tiles
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate tile ids detected. Every tile id must be unique:\n  " +
                string.Join("\n  ", duplicates));
        }

        _reloadSubscription = ChangeToken.OnChange(configuration.GetReloadToken, () => Changed?.Invoke());
    }

    /// <summary>Raised when configuration reloads and a tile's availability may have changed.</summary>
    public event Action? Changed;

    /// <summary>Every tile that is available to <paramref name="isAdmin"/>, in catalog order.</summary>
    public IReadOnlyList<TileDefinition> Available(bool isAdmin) =>
        _tiles.Where(t => (isAdmin || !t.AdminOnly) && IsAvailable(t.EnabledConfigPath)).ToList();

    /// <summary>Every available tile, including admin-only ones (the Tile Editor filters per user).</summary>
    public IReadOnlyList<TileDefinition> All =>
        _tiles.Where(t => IsAvailable(t.EnabledConfigPath)).ToList();

    /// <summary>The available tile with <paramref name="id"/>, or null.</summary>
    public TileDefinition? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Distinct categories in catalog order.</summary>
    public IReadOnlyList<string> Categories(bool isAdmin) =>
        Available(isAdmin)
            .Select(t => t.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Whether a config-gated tile is currently enabled (no path = always enabled).</summary>
    public bool IsAvailable(string? enabledConfigPath) =>
        enabledConfigPath is null || _configuration.IsServiceEnabled(enabledConfigPath);

    public void Dispose() => _reloadSubscription.Dispose();
}
